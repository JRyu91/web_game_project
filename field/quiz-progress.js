const memory = new Map();
const missing = new Set([51, 73, 95, 147, 382, 933]);
const number = n => Number.isSafeInteger(n) && n >= 0 && n < Number.MAX_SAFE_INTEGER;
const question = n => Number.isInteger(n) && n >= 1 && n <= 954 && !missing.has(n);
function validState(s) {
  if (!s || s.version !== 'saa-c03-v3-first10' || !number(s.count) || !question(s.current) || !s.records || typeof s.records !== 'object' || Array.isArray(s.records)) return false;
  if (Object.keys(s.records).length > 948) return false;
  return Object.entries(s.records).every(([id, r]) => /^[1-9]\d*$/.test(id) && question(Number(id)) && r && typeof r.correct === 'boolean' && number(r.attempts) && r.attempts > 0 && number(r.due) && Array.isArray(r.selected) && r.selected.length >= 1 && r.selected.length <= 6 && r.selected.every(a => typeof a === 'string' && /^[A-F]$/.test(a)) && new Set(r.selected).size === r.selected.length && (r.markedWrong === undefined || r.markedWrong === true && r.correct === false));
}
async function load(db, id) {
  if (!db) return memory.get(id) || { revision: 0, state: null };
  const result = await db.query('SELECT quiz_progress FROM accounts WHERE user_id = $1', [id]);
  if (!result.rowCount) throw Error('account missing');
  return result.rows[0].quiz_progress || { revision: 0, state: null };
}
async function save(db, id, revision, state) {
  const value = { revision: revision + 1, state };
  if (!db) {
    if ((memory.get(id)?.revision || 0) !== revision) return null;
    memory.set(id, JSON.parse(JSON.stringify(value))); return value;
  }
  const result = await db.query(`UPDATE accounts SET quiz_progress = $2::jsonb
    WHERE user_id = $1 AND COALESCE((quiz_progress->>'revision')::bigint, 0) = $3 RETURNING user_id`, [id, JSON.stringify(value), revision]);
  return result.rowCount ? value : null;
}
module.exports = { validState, load, save };
