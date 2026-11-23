-- Folds a payment outcome into a player's profile and returns the whole profile hash.
--
-- KEYS[1]  profile hash
-- ARGV[1]  field to increment: "approved" or "chargebacks"
-- ARGV[2]  unix ms of the approval to store as lastApprovedAt, or ""

redis.call('HINCRBY', KEYS[1], ARGV[1], 1)
if ARGV[2] ~= '' then
  redis.call('HSET', KEYS[1], 'lastApprovedAt', ARGV[2])
end
return redis.call('HGETALL', KEYS[1])
