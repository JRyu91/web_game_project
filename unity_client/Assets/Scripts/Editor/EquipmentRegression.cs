using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Game.Data;
using Game.Gameplay;
using Game.Network;
using Game.Rendering;
using Object = UnityEngine.Object;

namespace Game.EditorTools {
public static class EquipmentRegression {
    static readonly string[] Clips = { "idle", "walk", "attack", "attack1", "attack2", "hurt", "dead" };
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static IEnumerable<(string clip, int index)> Frames(string body) {
        foreach (var clip in Clips) foreach (int index in ClipTable.Get(body + "/" + clip).frames) yield return (clip, index);
    }
    static PlayerController Player(string gender) {
        var go = new GameObject("EquipmentActor");
        var player = go.AddComponent<PlayerController>();
        player.Init(false, gender, () => Array.Empty<MonsterController>());
        return player;
    }
    static SaveData Loadout(string kind, int weapon, int helmet, int armor) => new SaveData {
        level = 100, weaponKind = kind, weaponTier = weapon, helmetTier = helmet, armorTier = armor,
        gearClass = kind == "staff" ? "mage" : "warrior"
    };
    static SpriteRenderer Part(PlayerController p, string name) => p.GetComponentsInChildren<SpriteRenderer>(true).First(r => r.name == name);
    static readonly Dictionary<Sprite,string> PoseKeys = new Dictionary<Sprite,string>();
    [Serializable] class Atlas { public string path; }
    [Serializable] class MaleManifest { public Atlas defaultHead, defaultBody, staffDefaultBody, swordTrail; public Atlas[] helmets, armors; }
    [Serializable] class MaleExample { public int h,a; public string clip,kind; public int[] frames,ms; }
    [Serializable] class MaleExampleFile { public MaleExample[] examples; }
    static MaleManifest _maleAssets; static bool _maleAssetsLoaded;
    static MaleManifest MaleAssets {
        get { if (!_maleAssetsLoaded) { _maleAssetsLoaded = true; var file = Resources.Load<TextAsset>("Config/male_wearable_manifest"); if (file != null) _maleAssets = JsonUtility.FromJson<MaleManifest>(file.text); } return _maleAssets; }
    }
    static string TierTexture(bool male, string slot, int tier) {
        var file = male ? MaleAssets : null;
        var entries = slot == "Helmet" ? file?.helmets : file?.armors;
        return entries != null && tier < entries.Length && entries[tier] != null && !string.IsNullOrEmpty(entries[tier].path)
            ? Path.GetFileName(entries[tier].path) : (slot == "Helmet" ? "h_" : "a_") + tier.ToString("00");
    }
    static string DisplayKey(Sprite sprite) {
        if (PoseKeys.Count == 0) foreach (string gender in new[] {"main_m","main_f"}) foreach (var f in Frames(gender)) {
            string key=$"{gender}/{f.clip}/frame_{f.index:00}";
            PoseKeys[Resources.Load<Sprite>(ActorScale.PlayerRoot+"/"+key)] = key;
        }
        Require(PoseKeys.TryGetValue(sprite,out var keyFound),"Unknown displayed body pose"); return keyFound;
    }
    static void Parts(PlayerController p, int helmet, int armor) {
        foreach (var part in new[] { (name: "Helmet", tier: helmet), (name: "Armor", tier: armor) }) {
            var sr = Part(p, part.name);
            Require(sr.enabled == (part.tier >= 0), part.name + " visibility " + part.tier);
            if (part.tier >= 0) {
                Require(sr.sprite != null, part.name + " missing sprite");
                var body = p.GetComponent<SpriteRenderer>().sprite;
                Require(sr.sprite.pixelsPerUnit == 40, part.name + " PPU");
                Require(sr.sprite.name == DisplayKey(body), part.name + " wrong displayed pose " + sr.sprite.name);
                string texture = TierTexture(p.GetComponent<FrameAnimator>().SourceKey == "main_m", part.name, part.tier);
                Require(sr.sprite.texture.name == texture, part.name + " wrong tier texture " + sr.sprite.texture.name);
                Require(sr.flipX == p.GetComponent<SpriteRenderer>().flipX, part.name + " facing");
            }
        }
    }
    static void Weapon(PlayerController p, string kind, int tier) {
        var anim=p.GetComponent<FrameAnimator>();var gear=p.GetComponent<GearAttachment>();
        var weapon=Part(p,"Weapon");var fx=Part(p,"WeaponFx");
        bool carry=anim.Clip=="idle"||anim.Clip=="walk";
        bool show=carry||anim.Clip.StartsWith("attack");
        Require(weapon.enabled==(show&&!anim.Reacting),"Weapon visibility "+anim.SourceKey+"/"+anim.Clip);
        if(!show||anim.Reacting){Require(!fx.enabled,"Hidden weapon FX remains");return;}
        var def=(kind=="staff"?GameData.Staves:GameData.Swords)[tier];
        string dir="Sprites/WeaponsDir/"+GearAttachment.Folder(Path.GetFileName(def.spritePath))+"/";
        string sprite="d_sheath";
        if(!carry) {
            string key=anim.SourceKey+"/"+anim.Clip+"/"+anim.SourceIndex;
            var entry=Regex.Match(Resources.Load<TextAsset>("Config/hand_table").text,"\""+key+"\":\\s*\\{([^}]*)");
            Require(entry.Success,"Missing attack hand "+key);
            int Number(string k) => int.Parse(Regex.Match(entry.Groups[1].Value,"\""+k+"\":\\s*(-?\\d+)").Groups[1].Value);
            sprite="d"+Number("dir");
            var body=p.GetComponent<SpriteRenderer>().sprite;
            var local=new Vector2(Mathf.Round(Number("x")-body.pivot.x)/40f,Mathf.Round(body.rect.height-Number("y")-body.pivot.y)/40f);
            if(p.GetComponent<SpriteRenderer>().flipX)local.x=-local.x;
            Require(Vector2.Distance(gear.HandSlot.localPosition,local)<.0001f,"Attack grip misplaced "+key);
        }
        Require(weapon.sprite==Resources.Load<Sprite>(dir+sprite),"Wrong weapon sprite "+dir+sprite);
        Require(weapon.flipX==p.GetComponent<SpriteRenderer>().flipX,"Weapon mirror mismatch");
        Require(weapon.sortingOrder>=4&&weapon.sortingOrder<=8&&weapon.sortingOrder!=6,"Weapon/body order collision");
        bool hasFx=Resources.Load<Sprite>(dir+(carry?"fx_sheath_0":"fx_"+sprite+"_0"))!=null;
        Require(fx.enabled==hasFx,"Weapon effect visibility "+dir+sprite);
        Require(float.IsFinite(gear.Muzzle.x)&&float.IsFinite(gear.Muzzle.y),"Invalid projectile muzzle");
    }
    static void MaleParts(PlayerController p, int helmet, int armor, bool staff) {
        var source = p.GetComponent<SpriteRenderer>(); var file = MaleAssets;
        Parts(p, helmet, armor);
        Require(file != null, "Missing male replacement manifest; legacy overlays are not a pass");
        bool wearing = helmet >= 0 || armor >= 0;
        bool attackPose = DisplayKey(source.sprite).Split('/')[1].StartsWith("attack");
        bool cleanStaffBody = staff && attackPose;
        Require(source.enabled == !(wearing || cleanStaffBody), "Source body must remain geometry-only while wearing/casting");
        Require(Part(p,"Body").enabled == (wearing ? armor < 0 : cleanStaffBody), "Default body visibility");
        Require(Part(p,"Head").enabled == (wearing && helmet < 0), "Default head visibility");
        bool trail = wearing && !staff && attackPose;
        Require(Part(p,"SwordTrail").enabled == trail, "Sword trail visibility or missing atlas");
        if (!wearing) return;
        foreach (var slot in new[] {"Body","Head","Armor","Helmet","SwordTrail"}) {
            var sr = Part(p,slot); if (!sr.enabled) continue;
            string expected = slot == "Body" ? (staff ? file.staffDefaultBody.path : file.defaultBody.path) : slot == "Head" ? file.defaultHead.path : slot == "SwordTrail" ? file.swordTrail.path :
                (slot == "Armor" ? file.armors[armor].path : file.helmets[helmet].path);
            Require(sr.sprite.texture.name == Path.GetFileName(expected), "Legacy fallback or wrong replacement " + slot);
            Require(sr.sprite.name == DisplayKey(source.sprite), "Replacement pose " + slot);
            Require(sr.flipX == source.flipX && sr.color == source.color, "Replacement facing/color " + slot);
            Require(sr.sharedMaterial.mainTexture == sr.sprite.texture, "Replacement texture binding " + slot);
            Require(sr.sharedMaterial.GetFloat("_Flash") == source.sharedMaterial.GetFloat("_Flash"), "Replacement flash " + slot);
        }
    }
    public static void RunMaleRepresentative() {
        try {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var file=MaleAssets;Require(file!=null,"Male manifest missing");
            bool identityGpu=Environment.GetCommandLineArgs().Contains("--male-identity-gpu");
            string output=Path.GetFullPath(Path.Combine(Application.dataPath,"../../unity_review/stage3/team_v6/f_round/code/final"));
            Shader.SetGlobalVector("_CharGrade",Vector4.zero);Shader.SetGlobalColor("_ActorOutline",new Color(.035f,.025f,.05f,1));
            var loadouts=new List<(int h,int a)>{(-1,2),(-1,5),(-1,21)};
            foreach(int h in new[]{0,2,5,15,21,24,30}) if(file.helmets[h]!=null&&!string.IsNullOrEmpty(file.helmets[h].path)) {
                loadouts.Add((h,-1));loadouts.Add((h,h<5?2:h<21?5:21));
            }
            if(file.helmets[2]!=null)loadouts.Add((2,21));
            foreach (var pair in new[] { (h:-1,a:15), (h:2,a:15) }) loadouts.Add(pair);
            for (int tier=0;tier<37;tier++) if (!loadouts.Contains((tier,tier))) loadouts.Add((tier,tier));
            if(identityGpu) {
                loadouts.Clear();
                var tiers=new[]{16,17,18,19,20,32,33,34,35,36};
                for(int i=0;i<tiers.Length;i++) {
                    int tier=tiers[i];
                    loadouts.Add((tier,tier));loadouts.Add((tier,-1));loadouts.Add((-1,tier));
                    loadouts.Add((tier,tiers[(i+1)%tiers.Length]));
                }
            }
            foreach(var loadout in loadouts) {
                var actors=new List<PlayerController>();var cam=new GameObject("MaleRepresentativeCamera").AddComponent<Camera>();
                const int cell=256,cols=14,rows=5;var rt=new RenderTexture(cell*cols,cell*rows,24);
                Texture2D texture=null;var active=RenderTexture.active;
                try {
                    cam.orthographic=true;cam.orthographicSize=cell*rows/80f;cam.transform.position=new Vector3(cell*cols/80f,cell*rows/80f,-10);
                    cam.clearFlags=CameraClearFlags.SolidColor;cam.backgroundColor=identityGpu?Color.clear:new Color(.13f,.12f,.16f);cam.targetTexture=rt;
                    string kind=loadout.a>=21||loadout.h>=21?"staff":"sword";int index=0;
                    foreach(int face in new[]{1,-1})foreach(var pose in Frames("main_m")) {
                        var p=Player("male");actors.Add(p);p.ApplySave(Loadout(kind,10,loadout.h,loadout.a));
                        p.GetComponent<FrameAnimator>().Still(pose.clip,pose.index);p.GetComponent<ActorVisual>().Refresh();
                        p.GetComponent<GearAttachment>().SetFace(face);MaleParts(p,loadout.h,loadout.a,kind=="staff");Weapon(p,kind,10);
                        p.transform.position=new Vector3((index%cols*cell+cell/2f)/40f,((rows-1-index/cols)*cell+20)/40f-SpriteBBox.Get(p.GetComponent<SpriteRenderer>().sprite).yMin,0);index++;
                    }
                    cam.Render();RenderTexture.active=rt;texture=new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false);
                    texture.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);texture.Apply();
                    if(identityGpu) {
                        var equipped=texture.GetPixels32();
                        foreach(var p in actors) {Part(p,"Helmet").enabled=false;Part(p,"Armor").enabled=false;}
                        cam.Render();texture.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);texture.Apply();
                        var withoutGear=texture.GetPixels32();
                        for(int n=0;n<index;n++) {
                            int visible=0,changed=0,x0=n%cols*cell,y0=(rows-1-n/cols)*cell;
                            for(int y=y0;y<y0+cell;y++)for(int x=x0;x<x0+cell;x++) {
                                int pixel=y*rt.width+x;
                                if(equipped[pixel].a>0)visible++;
                                if(!equipped[pixel].Equals(withoutGear[pixel]))changed++;
                            }
                            Require(visible>30&&changed>10,$"GPU gear invisible h{loadout.h}/a{loadout.a}/pose{n}");
                        }
                    } else File.WriteAllBytes(Path.Combine(output,$"male_representative_h{loadout.h:D2}_a{loadout.a:D2}.png"),texture.EncodeToPNG());
                }finally{RenderTexture.active=active;if(texture!=null)Object.DestroyImmediate(texture);cam.targetTexture=null;Object.DestroyImmediate(rt);Object.DestroyImmediate(cam.gameObject);foreach(var p in actors)Object.DestroyImmediate(p.gameObject);}
            }
            Debug.Log(identityGpu?$"PASS male identity: {loadouts.Count*70} GPU poses, independent/mixed slots, both faces, source geometry/weapon/layer binding, visible gear pixels vs hidden gear; visual review separate":$"PASS male representative: {loadouts.Count*70} GPU poses, ready head families and 3 armor families, independent/mixed slots, both faces, source geometry/weapon/layer binding; visual review separate");EditorApplication.Exit(0);
        }catch(Exception ex){Debug.LogException(ex);EditorApplication.Exit(1);}
    }
    public static void RunMaleReplacement() {
        try {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var file = MaleAssets;
            Require(file != null && file.helmets?.Length == 37 && file.armors?.Length == 37, "Male replacement needs all 37 tiers per slot");
            foreach (var atlas in file.helmets.Concat(file.armors).Concat(new[] {file.defaultHead,file.defaultBody,file.staffDefaultBody,file.swordTrail})) {
                Require(atlas != null && !string.IsNullOrEmpty(atlas.path), "Unmade male tier/default layer");
                var ti = AssetImporter.GetAtPath("Assets/Resources/"+atlas.path+".png") as TextureImporter;
                Require(ti != null && ti.filterMode == FilterMode.Point && !ti.mipmapEnabled && ti.spritePixelsPerUnit == 40 && ti.textureCompression == TextureImporterCompression.Uncompressed, "Male import " + atlas.path);
            }
            var p = Player("male");
            try {
                p.ApplySave(Loadout("sword",10,-1,-1));
                var anim = p.GetComponent<FrameAnimator>(); var gear = p.GetComponent<GearAttachment>(); var sr = p.GetComponent<SpriteRenderer>();
                var poses = Frames("main_m").ToArray(); Require(poses.Length == 35,"Male selected frame count");
                foreach (var kind in new[] {"sword","staff"}) {
                    p.ApplySave(Loadout(kind,10,-1,-1));
                    foreach (var pose in poses) foreach (int face in new[] {1,-1}) {
                        anim.Still(pose.clip,pose.index); gear.SetFace(face);
                        var original = sr.sprite; var pivot = original.pivot;
                        foreach (int tier in Enumerable.Range(0,37)) foreach (var slots in new[] {(h:tier,a:-1),(h:-1,a:tier),(h:tier,a:(tier*17+3)%37)}) {
                            gear.SetWear(slots.h,slots.a); MaleParts(p,slots.h,slots.a,kind=="staff"); Weapon(p,kind,10);
                            Require(sr.sprite==original && sr.sprite.pivot==pivot,"Wearing changed source geometry");
                        }
                        gear.SetWear(-1,-1); MaleParts(p,-1,-1,kind=="staff");
                    }
                    foreach(var clip in Clips) {
                        gear.SetWear(5,15); anim.Play(clip,false); int guard=0; var seen=new HashSet<string>();
                        do { gear.Apply(); MaleParts(p,5,15,kind=="staff"); seen.Add(DisplayKey(sr.sprite)); anim.Tick(.001f); Require(++guard<10000,"Male animation stuck "+clip); } while(!anim.Done);
                        Require(seen.Count==ClipTable.Get("main_m/"+clip).frames.Distinct().Count(),"Male motion skipped replacement pose");
                    }
                }
                p.ApplySave(Loadout("sword",10,5,15)); anim.Play("attack",false); anim.React("hurt"); gear.Apply(); MaleParts(p,5,15,false);
                sr.sharedMaterial.SetFloat("_Flash",1); gear.Apply(); MaleParts(p,5,15,false); sr.sharedMaterial.SetFloat("_Flash",0); gear.Apply();
                foreach(var pose in poses) {
                    anim.Still(pose.clip,pose.index);
                    for(int h=0;h<37;h++) for(int a=0;a<37;a++) { gear.SetWear(h,a); MaleParts(p,h,a,false); }
                }
            } finally { Object.DestroyImmediate(p.gameObject); }
            Functional();
            CaptureMaleExamples();
            Debug.Log("PASS MaleReplacement: all 37 helmet/armor tiers, independent slots, 35 poses, both faces/weapons, 47915 mixed poses, timing/React/flash/source geometry, authoritative state/save/remote; art review is separate");
            EditorApplication.Exit(0);
        } catch(Exception ex) { Debug.LogException(ex); EditorApplication.Exit(1); }
    }
    static void CaptureMaleExamples() {
        string output=Path.GetFullPath(Path.Combine(Application.dataPath,"../../unity_review/stage3/team_v6/f_round/code/final"));
        var random=new System.Random(261010);
        var examples=new List<MaleExample>();
        Shader.SetGlobalVector("_CharGrade",Vector4.zero); Shader.SetGlobalColor("_ActorOutline",new Color(.035f,.025f,.05f,1));
        for(int n=0;n<6;n++) {
            // Two examples per material keep the corrected high plate and robe visible.
            int a=n<2 ? random.Next(2,5) : n<4 ? random.Next(15,21) : random.Next(21,37);
            string cls=GameData.Armors[a].cls;
            string gearClass=cls=="common" ? (random.Next(2)==0?"warrior":"mage") : cls;
            var helmets=Enumerable.Range(0,37).Where(h=>GameData.Helmets[h].level!=GameData.Armors[a].level && (GameData.Helmets[h].cls=="common" || GameData.Helmets[h].cls==gearClass)).ToArray();
            int h=helmets[random.Next(helmets.Length)];
            string clip=Clips[random.Next(Clips.Length)]; var def=ClipTable.Get("main_m/"+clip);
            string kind=gearClass=="mage"?"staff":"sword";
            examples.Add(new MaleExample {h=h,a=a,clip=clip,kind=kind,frames=def.frames,ms=def.ms});
            var actors=new List<PlayerController>(); var cam=new GameObject("MaleExampleCamera").AddComponent<Camera>();
            const int cell=256; var rt=new RenderTexture(cell*def.frames.Length,cell,24);
            try {
                cam.orthographic=true; cam.orthographicSize=cell/80f; cam.transform.position=new Vector3(rt.width/80f,cell/80f,-10);
                cam.clearFlags=CameraClearFlags.SolidColor;cam.backgroundColor=new Color(.13f,.12f,.16f);cam.targetTexture=rt;
                for(int j=0;j<def.frames.Length;j++) {
                    var p=Player("male");actors.Add(p);p.ApplySave(Loadout(kind,Math.Min(20,GameData.Armors[a].level/5),h,a));
                    p.GetComponent<FrameAnimator>().Still(clip,def.frames[j]);p.GetComponent<ActorVisual>().Refresh();p.GetComponent<GearAttachment>().Apply();
                    p.transform.position=new Vector3((j*cell+cell/2f)/40f,20/40f-SpriteBBox.Get(p.GetComponent<SpriteRenderer>().sprite).yMin,0);
                }
                cam.Render(); RenderTexture.active=rt;var texture=new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false);
                texture.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);texture.Apply();RenderTexture.active=null;
                File.WriteAllBytes(Path.Combine(output,$"male_example_{n}_h{h:D2}_a{a:D2}_{clip}.png"),texture.EncodeToPNG());Object.DestroyImmediate(texture);
                Debug.Log($"Male example {n}: helmet={h},armor={a},clip={clip},frames={string.Join(",",def.frames)},ms={string.Join(",",def.ms)}");
            } finally {cam.targetTexture=null;Object.DestroyImmediate(rt);Object.DestroyImmediate(cam.gameObject);foreach(var p in actors)Object.DestroyImmediate(p.gameObject);}
        }
        File.WriteAllText(Path.Combine(output,"male_examples.json"),JsonUtility.ToJson(new MaleExampleFile {examples=examples.ToArray()},true));
    }
    public static void RunEnhancementBatch() {
        try { EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); RunEnhancement(); Functional(); EditorApplication.Exit(0); }
        catch(Exception ex) { Debug.LogException(ex); EditorApplication.Exit(1); }
    }
    public static void RunEnhancement() {
        float time = ZoneController.AnimTime;
        var p = Player("male");
        try {
            ZoneController.AnimTime = 0;
            var gear = p.GetComponent<GearAttachment>(); var anim = p.GetComponent<FrameAnimator>();
            foreach (string kind in new[] {"sword", "staff"}) {
                Color previous = Color.clear;
                foreach (int enh in new[] {0, 6, 7, 10, 11, 14, 15, 25, 0}) {
                    var save = Loadout(kind, 20, -1, -1); save.weaponEnh = enh; p.ApplySave(save);
                    foreach (string clip in new[] {"idle", "walk", "attack", "attack1", "attack2"}) foreach (int face in new[] {-1, 1}) {
                        anim.Still(clip, clip.StartsWith("attack") ? 9 : 0); gear.SetFace(face); gear.Apply();
                        var weapon = Part(p,"Weapon");
                        Require(weapon.enabled, "Enhanced weapon hidden " + kind + "/" + clip);
                        Color color = weapon.sharedMaterial.GetColor("_EnhanceColor");
                        Require((color.a > 0) == (enh >= 7), "Enhancement boundary/reset " + enh);
                        if (enh >= 7 && enh < 11) Require(color.b > color.r, "Blue enhancement");
                        if (enh >= 11 && enh < 15) Require(color.r > color.b, "Red enhancement");
                        if (enh == 15) Require(color != previous, "Rainbow boundary");
                    }
                    previous = Part(p,"Weapon").sharedMaterial.GetColor("_EnhanceColor");
                }
            }
            // Render the real weapon shader: material values alone cannot catch GPU/WebGL shader failures.
            var cam = new GameObject("EnhancementCamera").AddComponent<Camera>();
            var rt = new RenderTexture(128,128,24); var pixels = new Texture2D(128,128,TextureFormat.RGBA32,false);
            var active = RenderTexture.active;
            try {
                cam.orthographic=true;cam.orthographicSize=2;cam.clearFlags=CameraClearFlags.SolidColor;cam.backgroundColor=Color.black;cam.targetTexture=rt;
                foreach(var sr in p.GetComponentsInChildren<SpriteRenderer>(true)) sr.enabled=false;
                var weapon=Part(p,"Weapon");
                weapon.gameObject.layer=31;cam.cullingMask=1<<31;
                foreach(string kind in new[] {"sword","staff"}) foreach(string clip in new[] {"idle","attack2"}) {
                    Color32[] baseline=null;
                    foreach(int enh in new[] {0,7,11,15}) {
                        var save=Loadout(kind,20,-1,-1);save.weaponEnh=enh;p.ApplySave(save);anim.Still(clip,clip=="idle"?0:9);gear.Apply();
                        foreach(var sr in p.GetComponentsInChildren<SpriteRenderer>(true))sr.enabled=sr==weapon;
                        cam.transform.position=weapon.bounds.center+new Vector3(0,0,-10);cam.Render();RenderTexture.active=rt;
                        pixels.ReadPixels(new Rect(0,0,128,128),0,0);pixels.Apply();var current=pixels.GetPixels32();
                        if(enh==0) baseline=current;
                        else Require(current.Where((color,index)=>!color.Equals(baseline[index])).Count()>10,"GPU enhancement invisible "+kind+"/"+clip+"/+"+enh);
                    }
                }
            } finally {RenderTexture.active=active;cam.targetTexture=null;Object.DestroyImmediate(pixels);Object.DestroyImmediate(rt);Object.DestroyImmediate(cam.gameObject);}
            var state = new InvState {level=100,equip=new InvEquip {weapon=1},inv=new[] {new InvItem {uid=1,slot="weapon",kind="staff",tier=20,enh=15}}};
            p.ApplyState(state); anim.Still("idle",0); gear.Apply();
            Color first = Part(p,"Weapon").sharedMaterial.GetColor("_EnhanceColor");
            ZoneController.AnimTime = 1; gear.Apply();
            Require(first != Part(p,"Weapon").sharedMaterial.GetColor("_EnhanceColor"), "Rainbow does not animate");
            state.inv[0].enh=6;p.ApplyState(state);gear.Apply();
            Require(Part(p,"Weapon").sharedMaterial.GetColor("_EnhanceColor").a==0,"Authoritative enhancement reset");
            Debug.Log("PASS Equipment enhancement: +6/7/10/11/14/15/25, sword/staff, carry/attack, left/right, authoritative update/reset, animated rainbow");
        } finally {ZoneController.AnimTime=time;Object.DestroyImmediate(p.gameObject);}
    }
    public static void Run() {
        try {
            RunEnhancement();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            foreach(string path in Directory.GetFiles(Application.dataPath+"/Resources/Sprites/Wearables","*.png")) {
                var ti=(TextureImporter)AssetImporter.GetAtPath("Assets"+path.Substring(Application.dataPath.Length));
                Require(ti!=null&&ti.spriteImportMode==SpriteImportMode.Single&&ti.spritePixelsPerUnit==40&&ti.filterMode==FilterMode.Point&&!ti.mipmapEnabled&&!ti.isReadable&&ti.textureCompression==TextureImporterCompression.Uncompressed,"Wearable import settings "+path);
            }
            string tag = Environment.GetEnvironmentVariable("WEAR_ROUND") ?? "r1";
            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../unity_review/stage3/team_v6/f_round/code/final"));
            var cam = new GameObject("EquipmentCamera").AddComponent<Camera>();
            const int cell = 256, cols = 14, rows = 5;
            cam.orthographic = true; cam.orthographicSize = cell * rows / 80f;
            cam.transform.position = new Vector3(cell * cols / 80f, cell * rows / 80f, -10);
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(.13f,.12f,.16f);
            var rt = new RenderTexture(cell * cols, cell * rows, 24);
            cam.targetTexture = rt;
            Shader.SetGlobalVector("_CharGrade", Vector4.zero);
            Shader.SetGlobalColor("_ActorOutline", new Color(.035f,.025f,.05f,1));
            int wearCells = 0, weaponCells = 0;
            foreach (string gender in new[] { "male", "female" }) {
                string body = gender == "male" ? "main_m" : "main_f";
                var frames = Frames(body).ToArray(); Require(frames.Length == 35, "Actual frames changed");
                for (int item = 0; item < GameData.Helmets.Length; item++) {
                    string kind = GameData.Helmets[item].cls == "mage" ? "staff" : "sword";
                    int wt = Math.Min(20, GameData.Helmets[item].level / 5);
                    Capture(item, "wear", kind, wt, item, item); wearCells += 70;
                }
                foreach (string kind in new[] { "sword", "staff" }) for (int item = 0; item < 21; item++) {
                    Capture(item, kind, kind, item, -1, -1); weaponCells += 70;
                }
                void Capture(int item, string label, string kind, int wt, int helmet, int armor) {
                    var actors = new List<PlayerController>();
                    try {
                        int n = 0;
                        foreach (var frame in frames) foreach (int face in new[] { 1, -1 }) {
                            var p = Player(gender); actors.Add(p); p.ApplySave(Loadout(kind, wt, helmet, armor));
                            var anim = p.GetComponent<FrameAnimator>(); var gear = p.GetComponent<GearAttachment>();
                            anim.Still(frame.clip, frame.index); gear.SetFace(face);
                            p.GetComponent<ActorVisual>().Refresh(); gear.Apply();
                            Parts(p, helmet, armor); Weapon(p,kind,wt);
                            if(kind=="staff"&&frame.clip.StartsWith("attack") && (body != "main_m" || MaleAssets == null || helmet < 0 && armor < 0)) {
                                var displayed=Part(p,"Body");
                                var clean=Resources.Load<Sprite>($"{ActorScale.PlayerRoot}/{body}/staff_{frame.clip}/frame_{frame.index:00}");
                                Require(clean!=null&&displayed.enabled&&displayed.sprite==clean,"Staff still shows baked sword slash");
                                Require(displayed.sharedMaterial.mainTexture==clean.texture,"Casting material uses wrong texture");
                            }
                            if (helmet >= 0) {
                                gear.SetWear(-1, armor); Parts(p,-1,armor);
                                gear.SetWear(helmet, -1); Parts(p,helmet,-1);
                                gear.SetWear(helmet, armor); Parts(p,helmet,armor);
                            }
                            p.transform.position = new Vector3(((n % cols) * cell + cell/2f) / 40f,
                                ((rows - 1 - n / cols) * cell + 20) / 40f - SpriteBBox.Get(p.GetComponent<SpriteRenderer>().sprite).yMin, 0);
                            n++;
                        }
                        var sequence=actors[0];var sequenceAnim=sequence.GetComponent<FrameAnimator>();
                        var sequenceGear=sequence.GetComponent<GearAttachment>();
                        foreach(var clip in Clips) {
                            sequenceAnim.Play(clip,false);
                            var seen=new HashSet<string>();
                            int guard=0;
                            do {
                                sequenceGear.Apply();Parts(sequence,helmet,armor);
                                seen.Add(DisplayKey(sequence.GetComponent<SpriteRenderer>().sprite));
                                sequenceAnim.Tick(.001f);
                                Require(++guard<10000,"Animation did not finish "+clip);
                            } while(!sequenceAnim.Done);
                            Require(seen.Count==ClipTable.Get(body+"/"+clip).frames.Distinct().Count(),"Animation skipped wearable pose "+clip);
                        }
                        sequenceAnim.Still(frames[0].clip,frames[0].index);sequenceGear.Apply();
                        SaveSheet("");
                        if(helmet>=0) {
                            foreach(var actor in actors) actor.GetComponent<GearAttachment>().SetWear(helmet,-1);
                            SaveSheet("_helmet");
                            foreach(var actor in actors) actor.GetComponent<GearAttachment>().SetWear(-1,armor);
                            SaveSheet("_armor");
                        }
                        void SaveSheet(string suffix) {
                        cam.Render(); RenderTexture.active = rt;
                        var tex = new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false);
                        tex.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);tex.Apply(); RenderTexture.active=null;
                        File.WriteAllBytes(Path.Combine(output,$"equipment_{tag}_{body}_{label}_{item:D2}{suffix}.png"),tex.EncodeToPNG());
                        Object.DestroyImmediate(tex);
                        }
                    } finally { foreach (var p in actors) Object.DestroyImmediate(p.gameObject); }
                }
            }
            Functional();
            cam.targetTexture = null; Object.DestroyImmediate(rt); Object.DestroyImmediate(cam.gameObject);
            Debug.Log($"PASS EquipmentRegression {tag}: {wearCells} wear poses (two slots), {weaponCells} weapon poses; actual 70 frames x both faces; independent GPU review required");
            EditorApplication.Exit(0);
        } catch (Exception ex) { Debug.LogException(ex); EditorApplication.Exit(1); }
    }
    static void Functional() {
        foreach (string gender in new[] { "male", "female" }) {
            var p = Player(gender);
            try {
                var gear=p.GetComponent<GearAttachment>(); var anim=p.GetComponent<FrameAnimator>();
                // Exercise the authoritative inv-state path rather than only SetWear.
                var state = new InvState { level=100, equip=new InvEquip {weapon=1,helmet=2,armor=3}, inv=new[] {
                    new InvItem {uid=1,slot="weapon",kind="sword",tier=20},
                    new InvItem {uid=2,slot="helmet",tier=20}, new InvItem {uid=3,slot="armor",tier=20}
                }};
                p.ApplyState(state); gear.Apply(); Parts(p,20,20);
                var saved=JsonUtility.FromJson<SaveData>(JsonUtility.ToJson(p.ToSaveData("Equipment",gender)));
                p.ApplySave(Loadout("staff",0,-1,-1)); Parts(p,-1,-1);
                p.ApplySave(saved); gear.Apply(); Parts(p,20,20);
                p.GetComponent<ActorVisual>().Flash(.1f);gear.Apply();
                Require(Part(p,"Armor").sharedMaterial.GetFloat("_Flash")==1&&Part(p,"Helmet").sharedMaterial.GetFloat("_Flash")==1,"Wearable hit flash mismatch");
                p.GetComponent<ActorVisual>().TickFlash(.11f);gear.Apply();
                Require(Part(p,"Armor").sharedMaterial.GetFloat("_Flash")==0,"Wearable flash leaked");
                var snap=p.GetSnapshot(); Require(snap.equip.helmet.tier==20 && snap.equip.armor.tier==20,"Snapshot wear persistence");
                anim.Play("attack",false);anim.React("hurt");gear.Apply();Parts(p,20,20);
                for(int i=0;i<30;i++){anim.Tick(.01f);gear.Apply();Parts(p,20,20);}
                anim.Play("dead",false); for(int i=0;i<60;i++){anim.Tick(.01f);gear.Apply();Parts(p,20,20);}
                anim.Play("idle");gear.Apply();Parts(p,20,20);
                foreach(var f in Frames(gender=="male"?"main_m":"main_f")) {
                    anim.Still(f.clip,f.index);
                    for(int h=0;h<37;h++) for(int a=0;a<37;a++){gear.SetWear(h,a);Parts(p,h,a);}
                }
                state.equip.helmet=state.equip.armor=0;p.ApplyState(state);gear.Apply();Parts(p,-1,-1);
            } finally { Object.DestroyImmediate(p.gameObject); }
        }
        var root=new GameObject("EquipmentRoster");
        try {
            var manager=root.AddComponent<GameManager>();
            var roster=new[]{new RosterEntry{id="equipment_peer",gender="female",zone="A",equip=new EquipMsg{
                weapon=new EquipWeaponMsg{kind="staff",tier=20,enh=11},helmet=new EquipWearMsg{tier=36},armor=new EquipWearMsg{tier=36}}}};
            var method=typeof(GameManager).GetMethod("UpdateRoster",BindingFlags.Instance|BindingFlags.NonPublic);
            method.Invoke(manager,new object[]{roster});
            var ghost=root.transform.Find("Ghost_equipment_peer");Require(ghost!=null,"Remote roster actor missing");
            var gear=ghost.GetComponent<GearAttachment>();gear.Apply();
            var remoteWeapon=ghost.GetComponentsInChildren<SpriteRenderer>().First(r=>r.name=="Weapon");
            Require(remoteWeapon.sharedMaterial.GetColor("_EnhanceColor").r>.9f,"Remote enhancement missing");
            roster[0].equip.weapon.enh=0;method.Invoke(manager,new object[]{roster});gear.Apply();
            Require(remoteWeapon.sharedMaterial.GetColor("_EnhanceColor").a==0,"Remote enhancement reset");
            Require(ghost.GetComponentsInChildren<SpriteRenderer>().Any(r=>r.name=="Helmet"&&r.enabled&&r.sprite!=null),"Remote helmet missing");
            Require(ghost.GetComponentsInChildren<SpriteRenderer>().First(r=>r.name=="Armor").sprite.texture.name=="a_36","Remote armor tier");
            roster[0].gender="male";roster[0].equip.helmet.tier=2;roster[0].equip.armor.tier=3;
            method.Invoke(manager,new object[]{roster});gear.Apply();
            Require(ghost.GetComponent<FrameAnimator>().SourceKey=="main_m","Remote gender switch");
            Require(ghost.GetComponentsInChildren<SpriteRenderer>().First(r=>r.name=="Helmet").sprite.texture.name==TierTexture(true,"Helmet",2),"Remote switched helmet tier");
            Require(ghost.GetComponentsInChildren<SpriteRenderer>().First(r=>r.name=="Armor").sprite.texture.name==TierTexture(true,"Armor",3),"Remote switched armor tier");
            var plate=ghost.GetComponent<ActorNameplate>();plate.SendMessage("LateUpdate");
            var helmetSr=ghost.GetComponentsInChildren<SpriteRenderer>().First(r=>r.name=="Helmet");
            Require(ghost.Find("Nameplate").localPosition.y >= helmetSr.transform.localPosition.y+helmetSr.sprite.bounds.max.y+.19f,"Nameplate overlaps worn helmet");
            roster[0].equip.helmet=roster[0].equip.armor=null;method.Invoke(manager,new object[]{roster});gear.Apply();
            Require(!ghost.GetComponentsInChildren<SpriteRenderer>(true).First(r=>r.name=="Helmet").enabled,"Remote unequip stale");
            Require(!ghost.GetComponentsInChildren<SpriteRenderer>(true).First(r=>r.name=="Armor").enabled,"Remote armor unequip stale");
        } finally {Object.DestroyImmediate(root);}
        Debug.Log("PASS Equipment functional: authoritative ApplyState, switch/unequip, save JSON restore, snapshot, React while attack, dead/idle, all 95830 helmet/armor pose combinations, remote roster update/unequip");
    }
}
}
