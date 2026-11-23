-- Records one payment attempt for one subject (a player, device, IP or card) and returns
-- the requested sliding-window aggregates. Runs atomically, so concurrent attempts for the
-- same subject can never read a count that skips or double-counts one of them.
--
-- KEYS[1]  attempts sorted set: member = attempt id, score = unix ms
-- KEYS[2]  amounts sorted set:  member = "attemptId|CUR|minorUnits", score = unix ms
--          (both keys share a {hash tag}, so the script is valid on Redis Cluster)
-- ARGV[1]  now, unix ms
-- ARGV[2]  retention in ms: the subject's longest window; older events are trimmed
-- ARGV[3]  attempt id
-- ARGV[4]  amount member, or "" when the subject has no sum windows
-- ARGV[5]  ISO 4217 currency to sum
-- ARGV[6…] pairs of (window ms, "c" for count | "s" for sum)
--
-- Windows are half-open: an event at t is inside a window w when now - w < t.

local now = tonumber(ARGV[1])
local retention = tonumber(ARGV[2])
local floor = now - retention

redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', floor)
-- NX: a retried assessment keeps its first timestamp and is counted once.
redis.call('ZADD', KEYS[1], 'NX', now, ARGV[3])
redis.call('PEXPIRE', KEYS[1], retention)

if ARGV[4] ~= '' then
  redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', floor)
  redis.call('ZADD', KEYS[2], 'NX', now, ARGV[4])
  redis.call('PEXPIRE', KEYS[2], retention)
end

local currency = ARGV[5]
local results = {}
for i = 6, #ARGV, 2 do
  local from = '(' .. string.format('%d', now - tonumber(ARGV[i]))
  if ARGV[i + 1] == 'c' then
    results[#results + 1] = redis.call('ZCOUNT', KEYS[1], from, '+inf')
  else
    local sum = 0
    local members = redis.call('ZRANGE', KEYS[2], from, '+inf', 'BYSCORE')
    for _, member in ipairs(members) do
      local cur, minor = string.match(member, '|(%u%u%u)|(%d+)$')
      if cur == currency then
        sum = sum + tonumber(minor)
      end
    end
    results[#results + 1] = sum
  end
end

return results
