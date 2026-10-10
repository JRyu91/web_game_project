// Disposable localhost-only inventory fixture for WebGL equipment acceptance.
if (process.env.DATABASE_URL || process.env.REDIS_URL) throw Error('Equipment fixture must use memory storage');
process.env.PORT = process.env.PORT || '18080';
const http = require('node:http'), listen = http.Server.prototype.listen;
http.Server.prototype.listen = function(port, ...args) { return listen.call(this, port, '127.0.0.1', ...args); };
const I = require('./items'), normalize = I.normalize;
I.normalize = raw => {
  if (raw?.inv) return normalize(raw);
  const s = normalize({...raw,level:100,weaponTier:20,helmetTier:20,armorTier:20});
  const add=(slot,kind,tier)=>{if(!s.inv.some(i=>i.slot===slot&&i.kind===kind&&i.tier===tier))s.inv.push({uid:s.nextUid++,slot,kind,tier,enh:0});};
  for(const kind of ['sword','staff'])for(let tier=0;tier<21;tier++)add('weapon',kind,tier);
  for(const slot of ['helmet','armor'])for(let tier=0;tier<37;tier++)add(slot,'',tier);
  s.bagExpansions=140; // 200 slots: leave room for drops so acceptance play does not pause.
  return normalize(s);
};
require('./server');
