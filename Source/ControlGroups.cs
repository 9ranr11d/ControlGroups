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

    public class ControlGroupsComponent : GameComponent
    {
        public const int Count = 10;
        // 폰뿐 아니라 건물·물건도 부대에 넣는다(스타처럼 생산 건물 지정). 예전 세이브의 List<Pawn> 참조도 Thing 으로 그대로 읽힌다
        public List<Thing>[] groups = new List<Thing>[Count];

        public ControlGroupsComponent(Game game)
        {
            for (int i = 0; i < Count; i++) groups[i] = new List<Thing>();
        }

        public static ControlGroupsComponent Instance => Current.Game?.GetComponent<ControlGroupsComponent>();

        public override void ExposeData()
        {
            // 저장 전에 죽은·폐기된 폰을 빼야 로드 때 참조 해석 오류가 안 난다
            if (Scribe.mode == LoadSaveMode.Saving)
                for (int i = 0; i < Count; i++) Prune(i);
            for (int i = 0; i < Count; i++)
            {
                List<Thing> list = groups[i];
                Scribe_Collections.Look(ref list, "group" + i, LookMode.Reference);
                if (Scribe.mode == LoadSaveMode.PostLoadInit || Scribe.mode == LoadSaveMode.LoadingVars)
                {
                    list = list ?? new List<Thing>();
                    list.RemoveAll(t => t == null);
                }
                groups[i] = list ?? new List<Thing>();
            }
        }

        public void Prune(int n)
        {
            // 건물은 부수거나 해체하면 Destroyed. 포장(미니파이)된 건물은 Destroyed 가 아니라 남겨 두고, 다시 설치하면 부대로 돌아온다
            groups[n].RemoveAll(t => t == null || t.Destroyed || t.Discarded || (t is Pawn p && p.Dead));
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
            // 월드 지도를 열었을 때만 막는다. WorldRendered 는 소행성·궤도 맵에서 행성을 배경으로 그릴 때도 참이라
            // 그걸 쓰면 우주 맵에서 부대 키가 전부 먹통이 된다
            if (WorldRendererUtility.WorldSelected) return;

            int n = NumberOf(e.keyCode);
            if (n < 0) return;
            ControlGroupsComponent comp = ControlGroupsComponent.Instance;
            if (comp == null) return;

            // 선택은 Ctrl. mac 은 Cmd 도 받는다 — Ctrl+숫자가 데스크탑 전환 단축키로 켜져 있으면 게임에 안 오기 때문
            bool recall = e.control || (Application.platform == RuntimePlatform.OSXPlayer && e.command);
            // 조합키는 하나만 눌렸을 때만 받는다. 맨 숫자는 게임 속도 키로 남긴다
            int mods = (recall ? 1 : 0) + (e.alt ? 1 : 0) + (e.shift ? 1 : 0);
            if (mods != 1) return;
            // 빈 부대여도 소비해야 조합키+숫자가 배속으로 새지 않는다
            e.Use();
            if (recall) Recall(comp, n);
            // 지정은 Alt(mac Option)+숫자, 추가는 Shift+숫자
            else Assign(comp, n, e.shift);
        }

        static int NumberOf(KeyCode k)
        {
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return k - KeyCode.Alpha0;
            if (k >= KeyCode.Keypad0 && k <= KeyCode.Keypad9) return k - KeyCode.Keypad0;
            return -1;
        }

        static void Assign(ControlGroupsComponent comp, int n, bool add)
        {
            // 구역(Zone)·플랜 같은 Thing 아닌 선택은 뺀다
            List<Thing> selected = new List<Thing>();
            foreach (object o in Find.Selector.SelectedObjects)
                if (o is Thing t && t.Spawned) selected.Add(t);
            // 빈 선택으로 부대가 날아가지 않게 아무것도 안 한다
            if (selected.Count == 0) return;
            List<Thing> g = comp.groups[n];
            if (add)
            {
                foreach (Thing t in selected)
                    if (!g.Contains(t)) g.Add(t);
                Messages.Message("CG_GroupAdded".Translate(n, g.Count), MessageTypeDefOf.SilentInput, historical: false);
                return;
            }
            g.Clear();
            g.AddRange(selected);
            Messages.Message("CG_GroupSet".Translate(n, g.Count), MessageTypeDefOf.SilentInput, historical: false);
        }

        static void Recall(ControlGroupsComponent comp, int n)
        {
            comp.Prune(n);
            List<Thing> g = comp.groups[n];
            if (g.Count == 0) return;

            Map map = Find.CurrentMap;
            List<Thing> here = g.Where(t => t.Spawned && t.Map == map).ToList();
            if (here.Count == 0)
            {
                // 다른 맵·캐러밴: 첫 멤버로 점프한 뒤 그 맵 멤버를 선택. 포장돼 들려 있는 건물만 남았으면 할 게 없다
                Thing target = g.FirstOrDefault(t => t.Spawned) ?? g.OfType<Pawn>().FirstOrDefault();
                if (target == null) return;
                CameraJumper.TryJumpAndSelect(new GlobalTargetInfo(target));
                Map m = Find.CurrentMap;
                here = g.Where(t => t.Spawned && t.Map == m).ToList();
                if (here.Count == 0) return;
            }
            Find.Selector.ClearSelection();
            foreach (Thing t in here) Find.Selector.Select(t, playSound: false, forceDesignatorDeselect: true);

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
