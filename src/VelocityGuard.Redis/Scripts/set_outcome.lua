-- Moves an attempt in the journal to a new outcome if the transition is allowed:
-- none -> approved | declined, approved -> chargeback. Same rules as OutcomeRules in C#.
--
-- KEYS[1]  attempt journal hash
-- ARGV[1]  target outcome: "approved", "declined" or "chargeback"
--
-- Returns { status, previous } with status 0 = not found, 1 = applied,
-- 2 = already recorded, 3 = transition not allowed.

if redis.call('EXISTS', KEYS[1]) == 0 then
  return { 0, '' }
end

local previous = redis.call('HGET', KEYS[1], 'outcome') or ''
local target = ARGV[1]

if previous == target then
  return { 2, previous }
end

local allowed = (previous == '' and (target == 'approved' or target == 'declined'))
  or (previous == 'approved' and target == 'chargeback')

if not allowed then
  return { 3, previous }
end

redis.call('HSET', KEYS[1], 'outcome', target)
return { 1, previous }
