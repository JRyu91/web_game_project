// field/server.js 의 GET/POST /save/:id 가 그대로 돌려주고 받는 JSON 뭉치.
// 구버전 JS 클라이언트와 필드가 같을 필요는 없다 — server.js 는 data 컬럼을 통째로 opaque JSON 으로만 다룬다.
using System;

namespace Game.Network {

[Serializable]
public class SaveData {
    public string name;
    public string gender;
    public int level = 1;
    public long exp;
    public long gold;
    public string weaponKind = "sword";
    public int weaponTier;
    public int weaponEnh;
    public int helmetTier = -1;
    public int helmetEnh;
    public int armorTier = -1;
    public int armorEnh;
    public string gearClass = "warrior";
    public int potionCount = 3;
    public int potionThreshold = 40;
}

// GET 응답은 {"data": <SaveData 또는 null>} 로 한 겹 감싸져 온다.
[Serializable]
public class SaveEnvelope { public SaveData data; }
}
