using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ControlGroups
{
    [StaticConstructorOnStartup]
    public static class HarmonyInit
    {
        static HarmonyInit()
        {
            new Harmony("9ranr11d.ControlGroups").PatchAll();
        }
    }

    public enum RecallMode { Alt, Bare }

    public class ControlGroupsSettings : ModSettings
    {
        public RecallMode recallMode = RecallMode.Alt;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref recallMode, "recallMode", RecallMode.Alt);
        }
    }

    public class ControlGroupsMod : Mod
    {
        public static ControlGroupsSettings Settings;

        public ControlGroupsMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<ControlGroupsSettings>();
        }

        public override string SettingsCategory() => "CG_SettingsCategory".Translate();

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var l = new Listing_Standard();
            l.Begin(inRect);
            if (l.RadioButton("CG_ModeAlt".Translate(), Settings.recallMode == RecallMode.Alt, 0f, "CG_ModeAltDesc".Translate()))
                Settings.recallMode = RecallMode.Alt;
            Text.Font = GameFont.Tiny;
            l.Label("CG_ModeAltDesc".Translate());
            Text.Font = GameFont.Small;
            l.Gap(6f);
            if (l.RadioButton("CG_ModeBare".Translate(), Settings.recallMode == RecallMode.Bare, 0f, "CG_ModeBareDesc".Translate()))
                Settings.recallMode = RecallMode.Bare;
            Text.Font = GameFont.Tiny;
            l.Label("CG_ModeBareDesc".Translate());
            Text.Font = GameFont.Small;
            l.End();
        }
    }

    public class ControlGroupsComponent : GameComponent
    {
        public const int Count = 10;
        public List<Pawn>[] groups = new List<Pawn>[Count];

        public ControlGroupsComponent(Game game)
        {
            for (int i = 0; i < Count; i++) groups[i] = new List<Pawn>();
        }

        public static ControlGroupsComponent Instance => Current.Game?.GetComponent<ControlGroupsComponent>();

        public override void ExposeData()
        {
            // 저장 전에 죽은·폐기된 폰을 빼야 로드 때 참조 해석 오류가 안 난다
            if (Scribe.mode == LoadSaveMode.Saving)
                for (int i = 0; i < Count; i++) Prune(i);
            for (int i = 0; i < Count; i++)
            {
                List<Pawn> list = groups[i];
                Scribe_Collections.Look(ref list, "group" + i, LookMode.Reference);
                if (Scribe.mode == LoadSaveMode.PostLoadInit || Scribe.mode == LoadSaveMode.LoadingVars)
                {
                    list = list ?? new List<Pawn>();
                    list.RemoveAll(p => p == null);
                }
                groups[i] = list ?? new List<Pawn>();
            }
        }

        public void Prune(int n)
        {
            groups[n].RemoveAll(p => p == null || p.Destroyed || p.Dead || p.Discarded);
        }
    }

    [HarmonyPatch(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI))]
    public static class Patch_UIRoot_Play_UIRootOnGUI
    {
        static float lastRecallTime = -10f;
        static int lastRecallGroup = -1;

        public static void Prefix()
        {
            Event e = Event.current;
            if (e == null || e.type != EventType.KeyDown) return;
            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
            if (Find.WindowStack.AnySearchWidgetFocused) return;
            // 이름 변경·키 지정 같은 모달 창이 떠 있으면 입력을 그 창에 맡긴다
            if (Find.WindowStack.Windows.Any(w => w.absorbInputAroundWindow)) return;
            if (WorldRendererUtility.WorldRendered) return;

            int n = NumberOf(e.keyCode);
            if (n < 0) return;
            ControlGroupsComponent comp = ControlGroupsComponent.Instance;
            if (comp == null) return;

            bool ctrl = e.control || e.command;
            if (ctrl)
            {
                Assign(comp, n, e.shift);
                e.Use();
                return;
            }
            if (e.shift) return;

            if ((ControlGroupsMod.Settings?.recallMode ?? RecallMode.Alt) == RecallMode.Alt)
            {
                if (!e.alt) return;
                Recall(comp, n);
                e.Use();
            }
            else
            {
                if (e.alt) return;
                comp.Prune(n);
                if (comp.groups[n].Count == 0) return;
                Recall(comp, n);
                e.Use();
            }
        }

        static int NumberOf(KeyCode k)
        {
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return k - KeyCode.Alpha0;
            if (k >= KeyCode.Keypad0 && k <= KeyCode.Keypad9) return k - KeyCode.Keypad0;
            return -1;
        }

        static void Assign(ControlGroupsComponent comp, int n, bool add)
        {
            List<Pawn> selected = Find.Selector.SelectedPawns;
            List<Pawn> g = comp.groups[n];
            if (add)
            {
                foreach (Pawn p in selected)
                    if (!g.Contains(p)) g.Add(p);
                Messages.Message("CG_GroupAdded".Translate(n, g.Count), MessageTypeDefOf.SilentInput, historical: false);
                return;
            }
            g.Clear();
            g.AddRange(selected);
            string key = g.Count == 0 ? "CG_GroupCleared" : "CG_GroupSet";
            Messages.Message(key.Translate(n, g.Count), MessageTypeDefOf.SilentInput, historical: false);
        }

        static void Recall(ControlGroupsComponent comp, int n)
        {
            comp.Prune(n);
            List<Pawn> g = comp.groups[n];
            if (g.Count == 0) return;

            Map map = Find.CurrentMap;
            List<Pawn> here = g.Where(p => p.Spawned && p.Map == map).ToList();
            if (here.Count == 0)
            {
                // 다른 맵·캐러밴: 첫 멤버로 점프한 뒤 그 맵 멤버를 선택
                Pawn target = g.FirstOrDefault(p => p.Spawned) ?? g[0];
                CameraJumper.TryJumpAndSelect(new GlobalTargetInfo(target));
                Map m = Find.CurrentMap;
                here = g.Where(p => p.Spawned && p.Map == m).ToList();
                if (here.Count == 0) return;
            }
            Find.Selector.ClearSelection();
            foreach (Pawn p in here) Find.Selector.Select(p, playSound: false, forceDesignatorDeselect: true);

            float now = Time.realtimeSinceStartup;
            if (lastRecallGroup == n && now - lastRecallTime <= 0.4f)
            {
                CameraJumper.TryJump(here[0]);
                lastRecallGroup = -1;
            }
            else
            {
                lastRecallGroup = n;
                lastRecallTime = now;
            }
        }
    }
}
