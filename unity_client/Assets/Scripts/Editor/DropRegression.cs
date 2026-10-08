using System;
using System.Collections;
using System.Reflection;
using System.IO;
using System.Linq;
using UnityEditor.SceneManagement;
using Game.Rendering;
using UnityEngine;
using Game.Gameplay;
using Game.Network;

namespace Game.EditorTools {
public static class DropRegression {
    static void Check(bool ok,string message) { if (!ok) throw new Exception("[DropRegression] "+message); }
    public static void Capture() {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        var root=new GameObject("DropCapture");
        var rt=new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);
        var texture=new Texture2D(1280,720,TextureFormat.RGBA32,false);
        try {
            var camera=new GameObject("Camera");camera.transform.SetParent(root.transform);
            var cam=camera.AddComponent<Camera>();cam.orthographic=true;cam.orthographicSize=9;cam.aspect=1280f/720;
            cam.targetTexture=rt;cam.backgroundColor=Color.black;cam.clearFlags=CameraClearFlags.SolidColor;
            var pixel=camera.AddComponent<UnityEngine.Rendering.Universal.PixelPerfectCamera>();
            pixel.assetsPPU=40;pixel.refResolutionX=1280;pixel.refResolutionY=720;
            var world=new GameObject("World");world.transform.SetParent(root.transform);
            var zone=world.AddComponent<ZoneController>();zone.Cam=cam;ZoneController.AnimTime=0;
            zone.SetZone(Zone.A,Tod.Day);zone.Layout(10);
            var actor=new GameObject("Player");actor.transform.SetParent(root.transform);actor.transform.position=new Vector3(10,0,0);
            var player=actor.AddComponent<PlayerController>();player.Init(false,"male",()=>Array.Empty<MonsterController>());
            var item=new InvItem {uid=77,slot="weapon",kind="sword",tier=0};
            var fx=root.AddComponent<WorldDropFeedback>();
            fx.Show(new InvDrop {tier=1,gold=9,stones=1,item=item},new InvState {pendingDrop=item,inv=Array.Empty<InvItem>()},new Vector3(12,0,0),actor.transform);
            fx.Tick(.2f);
            foreach(var visual in ActorVisual.All.ToArray()) if(visual!=null) visual.Refresh();
            cam.Render();cam.Render();
            RenderTexture.active=rt;texture.ReadPixels(new Rect(0,0,1280,720),0,0);texture.Apply();RenderTexture.active=null;
            Directory.CreateDirectory("Logs");File.WriteAllBytes("Logs/v304-drop-visual.png",texture.EncodeToPNG());
            Debug.Log("[DropRegression] capture Logs/v304-drop-visual.png: A/player/gold+stone bounce and pending weapon hold; visual review required");
            cam.targetTexture=null;
        } finally {
            RenderTexture.active=null;ZoneController.AnimTime=-1;
            UnityEngine.Object.DestroyImmediate(root);rt.Release();UnityEngine.Object.DestroyImmediate(rt);UnityEngine.Object.DestroyImmediate(texture);
        }
    }
    // Presentation fixtures only; no server writes, manual pickup or new reward calculation.
    public static void Run() {
        var root = new GameObject("DropRegression");
        try {
            var fx = root.AddComponent<WorldDropFeedback>();
            var target = new GameObject("Target"); target.transform.SetParent(root.transform);
            var item = new InvItem {uid=77,slot="weapon",kind="sword",tier=0};
            var reward = new InvDrop {tier=1,gold=9,stones=1,item=item};
            var state = new InvState {inv=new[]{item}};
            fx.Show(reward,state,new Vector3(12,0,0),target.transform);
            Check(fx.VisibleCount==3,"missing gold/stone/equipment assets");
            fx.Tick(1); Check(fx.VisibleCount==0,"automatic acquisition visuals did not finish");
            reward.sold=50; fx.Show(reward,state,Vector3.zero,target.transform);
            Check(fx.VisibleCount==2,"auto-sold equipment falsely appears as acquired equipment"); fx.Clear();
            reward.sold=0; state.inv=Array.Empty<InvItem>();state.pendingDrop=item;
            fx.Show(reward,state,Vector3.zero,target.transform);fx.Tick(1);
            Check(fx.VisibleCount==1&&fx.PendingUid==77,"full-bag equipment was falsely acquired");
            fx.SyncPending(state,Vector3.zero,target.transform);Check(fx.VisibleCount==1,"repeated inventory state duplicates pending visual");
            state.pendingDrop=null;state.inv=new[]{item};fx.SyncPending(state,Vector3.zero,target.transform);
            Check(fx.PendingUid==0&&fx.VisibleCount==1,"confirmed pending item cannot begin acquisition");
            fx.Tick(1);Check(fx.VisibleCount==0,"pending acquisition did not finish");
            Check(reward.gold==9&&reward.stones==1&&state.inv.Length==1,"visuals changed authoritative rewards");
            CheckManagerAck(root,fx);
            Debug.Log("[DropRegression] PASS: existing assets, auto acquisition, auto-sale, full-bag hold, single pending collection; no reward mutation");
        } finally { UnityEngine.Object.DestroyImmediate(root); }
    }
    static void CheckManagerAck(GameObject root,WorldDropFeedback fx) {
        var manager=root.AddComponent<GameManager>(); manager.enabled=false;
        var actor=new GameObject("AckPlayer");actor.transform.SetParent(root.transform);
        var player=actor.AddComponent<PlayerController>();player.enabled=false;
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(GameManager).GetField("_player",flags).SetValue(manager,player);
        typeof(GameManager).GetField("_dropFeedback",flags).SetValue(manager,fx);
        var records=(IDictionary)typeof(GameManager).GetField("_spawns",flags).GetValue(manager);
        var type=typeof(GameManager).GetNestedType("SpawnRegistration",BindingFlags.NonPublic);
        var record=Activator.CreateInstance(type,true);
        var corpse=new GameObject("AckMonster");corpse.transform.SetParent(root.transform);
        corpse.transform.position=new Vector3(12,0,0);
        var monster=corpse.AddComponent<MonsterController>();monster.enabled=false;
        type.GetField("monster").SetValue(record,monster);type.GetField("killSeq").SetValue(record,19);records.Add(1,record);
        typeof(GameManager).GetMethod("MonsterDied",flags).Invoke(manager,new object[]{monster});
        UnityEngine.Object.DestroyImmediate(corpse); // Position must survive corpse disposal before ACK.
        var resolve=typeof(GameManager).GetMethod("ResolveKill",flags);
        var ack=new InvMsg {req="kill",seq=18,ok=true,drop=new InvDrop {gold=9}};
        resolve.Invoke(manager,new object[]{ack});Check(fx.VisibleCount==0,"unmatched ACK displays a drop");
        ack.seq=19;ack.ok=false;ack.code="save_failed";resolve.Invoke(manager,new object[]{ack});
        Check(fx.VisibleCount==0&&records.Count==1,"failed save displays a drop or loses registration");
        type.GetField("killSeq").SetValue(record,20);ack.seq=20;ack.ok=true;
        resolve.Invoke(manager,new object[]{ack});Check(fx.VisibleCount==1&&records.Count==0,"confirmed retry did not display once");
        Check(Mathf.Abs(fx.GetComponentInChildren<SpriteRenderer>().transform.position.x-11.6f)<.001f,"drop lost captured death position");
        resolve.Invoke(manager,new object[]{ack});Check(fx.VisibleCount==1,"duplicate success ACK displays twice");
        fx.Tick(1);resolve.Invoke(manager,new object[]{ack});Check(fx.VisibleCount==0,"late duplicate ACK replays acquisition");
        Debug.Log("[DropRegression] PASS: GameManager real seq gate, save failure retry, captured corpse position and duplicate ACK suppression (simulated responses)");
    }
}
}
