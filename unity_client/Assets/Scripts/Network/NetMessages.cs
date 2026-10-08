// field/server.js·field/net.js 프로토콜 메시지 — Unity 내장 JsonUtility 로 주고받기 위한 고정 DTO.
// (System.Text.Json/Newtonsoft 는 이 Unity 런타임 프로파일에 없어서 쓰지 않음 — JsonUtility면 충분하다.)
using System;

namespace Game.Network {

[Serializable] public class EquipWeaponMsg { public string kind; public int tier; public int enh; }
[Serializable] public class EquipMsg { public EquipWeaponMsg weapon; }
[Serializable] public class RosterEntry { public string zone; public bool bot; public string id; public string name; public int level; public string gender; public EquipMsg equip; }

[Serializable] public class TypeOnly { public string type; }
[Serializable] public class SavePreferences { public int potionThreshold; }

[Serializable] public class JoinMsg { public string type = "join"; public string id; public string name; public int level; public string gender; public bool resume; }
[Serializable] public class ChatOutMsg { public string type = "chat"; public string text; }
[Serializable] public class SwitchMsg { public string type = "switch"; public int to; }
[Serializable] public class BotMsg { public string type = "bot"; public int delta; public int level; }
[Serializable] public class PosOutMsg { public string type = "pos"; public float x; public int face; }

[Serializable] public class SkillTimer { public string key; public float remaining; }
[Serializable] public class Snapshot { public SkillTimer[] skillTimers; public float potionCd, attackCd, hitDelay, regenAcc, respawnRemaining; public bool dead; public float x; public int face; public int hp; public int maxHp; public int level; public long exp; public EquipMsg equip; }
[Serializable] public class StateOutMsg { public string type = "state"; public int level; public Snapshot snapshot; }

[Serializable] public class WelcomeMsg { public string type; public string id; public string channel; public int index; public int cap; public RosterEntry[] roster; public bool resumed; }
[Serializable] public class RosterMsg { public string type; public RosterEntry[] roster; }
[Serializable] public class ChatInMsg { public string type; public string who; public string text; }
[Serializable] public class SystemMsg { public string type; public string text; }
[Serializable] public class ResumeMsg { public string type; public Snapshot state; }
[Serializable] public class PosInMsg { public string type; public string id; public float x; public int face; }
[Serializable] public class TransferMsg { public string type; public int to; public string @base; public string reason; }
[Serializable] public class FullMsg { public string type; public int cap; }
[Serializable] public class DrainMsg { public string type; public string text; }
[Serializable] public class ClosedMsg { public string type; public string reason; }

// F안 권위 서버(f_round/server/API.md): 요청 1종(필드 안 쓰는 건 기본값으로 감) + 응답 1종 inv
[Serializable] public class SpawnMsg { public string type; public string receipt, code; public int monsterId, tier; public bool ok; }
[Serializable] public class InvReq { public int monsterId; public string receipt; public string type; public int seq; public int tier; public string slot; public int uid; public string item; public int qty; public int level; public bool enabled; public int token; public bool spawned; public string zone; }
[Serializable] public class InvCost { public int stones; public long gold; }
[Serializable] public class InvItem { public int uid; public string slot; public string kind; public int tier; public int enh; public string name; public int level; public long sell; public InvCost cost; }
[Serializable] public class InvEquip { public int weapon; public int helmet; public int armor; }
[Serializable] public class Skillbook { public string key, name, weapon; public int level; public long price; public bool owned, available; }
[Serializable] public class PendingSummon { public int tier, token; }
[Serializable] public class InvState { public bool HasPendingDrop => pendingDrop != null && pendingDrop.uid > 0; public PendingSummon pendingSummon; public InvItem pendingDrop; public string zone; public int[] mapMinLevels; public string[] skills; public Skillbook[] skillbooks; public bool autoSell; public int autoSellLevel; public long expandCost; public bool expandAvailable; public int level; public long exp; public long gold; public int stones; public int potions; public long potionPrice; public int invCap; public int killCountT19; public int killCountT20; public InvEquip equip; public InvItem[] inv; }
[Serializable] public class InvDrop { public int tier; public long gold; public long exp; public int stones; public int levelUp; public long sold; public InvItem item; }
[Serializable] public class InvEnh { public int uid; public int from; public int to; public string result; public InvCost cost; }
[Serializable] public class InvSkip { public int uid; public string code; }
[Serializable] public class InvSell { public int[] sold; public long gold; public InvSkip[] skipped; }
[Serializable] public class SellManyReq { public string type = "sell"; public int seq; public int[] uids; } // 일괄 판매: uids 가 있으면 서버가 배열 처리(단건은 InvReq — uids 키가 아예 없어야 함)
[Serializable] public class DisassembleManyReq { public string type = "disassemble"; public int seq; public int[] uids; }
[Serializable] public class InvDisassemble { public int[] removed; public int stones; public InvSkip[] skipped; }
[Serializable] public class InvMsg { public InvDisassemble disassemble; public string type; public string req; public int seq; public bool ok; public string code; public InvDrop drop; public InvEnh enh; public InvSell sell; public InvState state; }
}
