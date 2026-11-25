using System.Linq;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ControlGroups
{
    // 소집 표시 색 — 격자·정착민 바가 같이 쓴다
    public static class DraftMark
    {
        public static readonly Color Border = new Color(0.95f, 0.22f, 0.18f, 1f);
        public static readonly Color Fill = new Color(0.95f, 0.22f, 0.18f, 0.22f);
    }

    // 스타크래프트처럼 여러 개를 고르면 검사 창 바로 위에 고른 것들을 칸으로 늘어놓는다.
    // 클릭 = 그것만 선택, Shift+클릭 = 선택에서 빼기, Ctrl/Cmd+클릭 = 같은 종류만 남기기,
    // 칸 위 드래그 = 박스에 걸친 칸만 남기기, Shift+드래그 = 그 칸들 빼기.
    // 검사 창(MainTabWindow_Inspect) 안은 창 영역으로 잘리므로, 단일 선택 때 탭을 창 위에 그리는 ExtraOnGUI 에 붙인다.
    // ExtraOnGUI 는 WindowStack 안에서 Selector 보다 먼저 불리므로, 여기서 클릭을 Use 하면 지도 선택·드래그 박스로 새지 않는다
    [HarmonyPatch(typeof(InspectPaneUtility), nameof(InspectPaneUtility.ExtraOnGUI))]
    public static class Patch_InspectPane_SelectionGrid
    {
        const float Cell = 44f;
        // 메카·동물은 같은 종류면 아이콘이 똑같아 누가 누군지 모른다 → 칸 아래 이름표 (사람도 같이, 줄 높이를 맞추려고)
        // 고정 14px 은 한국어 Tiny 글자 윗부분이 잘렸다(2026-11-23 스크린샷) → 실제 글꼴 줄 높이를 쓴다
        static float NameHeight => Text.LineHeightOf(GameFont.Tiny);
        // 막대 세 줄: 체력 / 허기·전력 / 수면 (각 3px, 위·사이 1px)
        const float OneBar = 3f;
        const float BarHeight = 3f * (1f + OneBar);
        static readonly Color RestColor = new Color(0.65f, 0.5f, 0.95f);
        const float Gap = 3f;

        // 정착민 바와 같은 기분 배경색 (바닐라 ColonistBarColonistDrawer.MoodBGTex 와 같은 값)
        static readonly Texture2D MoodBGTex = SolidColorMaterials.NewSolidColorTexture(new Color(0.4f, 0.47f, 0.53f, 0.44f));
        static readonly Color FoodColor = new Color(0.95f, 0.6f, 0.2f);
        static readonly Color EnergyColor = new Color(0.35f, 0.75f, 0.95f);
        static readonly Color LowColor = new Color(0.9f, 0.25f, 0.2f);

        // 접기 버튼으로 숨긴 상태. 게임 안에서만 유지(세이브·설정 저장 안 함)
        static bool minimized;
        const float TabHeight = 18f;
        const float Pad = 6f;
        const int MaxRows = 4;

        // 칸 크기로 따로 그린 초상화 — 머리·어깨가 칸에 차도록 바(1.28)보다 당겨 찍는다
        static readonly Vector2 PortraitSize = new Vector2(Cell, Cell);
        static readonly Vector3 PortraitOffset = new Vector3(0f, 0f, 0.35f);
        const float PortraitZoom = 1.7f;

        static readonly List<Thing> things = new List<Thing>();
        static readonly List<Rect> cellRects = new List<Rect>();

        // 격자 안 드래그 박스. 칸 위에서 누른 채 끌면 박스에 걸친 칸만 고른다 (Shift 면 그 칸들을 뺀다)
        const float DragThreshold = 5f;
        static bool dragging;
        static Vector2 dragStart;
        static readonly Color BoxColor = new Color(0.35f, 0.95f, 0.35f, 1f);

        public static void Postfix(IInspectPane pane)
        {
            // 격자가 사라지면(선택이 하나가 됨 등) 끌던 상태도 버린다
            if (!DrawGrid(pane)) dragging = false;
        }

        static bool DrawGrid(IInspectPane pane)
        {
            if (Current.ProgramState != ProgramState.Playing || WorldRendererUtility.WorldSelected) return false;
            if (!(pane is MainTabWindow_Inspect)) return false;
            // 구역 지정 같은 도구가 창 위에 자체 조작판을 그릴 때는 비켜 준다
            if (Find.DesignatorManager.SelectedDesignator != null) return false;
            Selector sel = Find.Selector;
            if (sel == null || sel.NumSelected <= 1) return false;

            things.Clear();
            List<object> selected = sel.SelectedObjects;
            for (int i = 0; i < selected.Count; i++)
                if (selected[i] is Thing t && t.Spawned) things.Add(t);
            if (things.Count <= 1) return false;

            float width = InspectPaneUtility.PaneWidthFor(pane);
            GameFont prevFont = Text.Font;

            // 접힌 상태면 검사 창 위 오른쪽에 작은 펼치기 탭만 둔다
            if (minimized)
            {
                Text.Font = GameFont.Tiny;
                string label = "▲ " + things.Count;
                float tabW = Text.CalcSize(label).x + 14f;
                Rect tab = new Rect(width - tabW, pane.PaneTopY - TabHeight, tabW, TabHeight);
                if (Widgets.ButtonText(tab, label)) minimized = false;
                TooltipHandler.TipRegion(tab, "CG_GridRestore".Translate(things.Count));
                Text.Font = prevFont;
                return false;
            }

            int perRow = Mathf.Max(1, Mathf.FloorToInt((width - Pad * 2f + Gap) / (Cell + Gap)));
            int maxShown = perRow * MaxRows;
            int shown = Mathf.Min(things.Count, maxShown);
            // 넘치면 마지막 칸을 "+n" 자리로 쓴다
            bool overflow = things.Count > maxShown;
            if (overflow) shown = maxShown - 1;
            int slots = overflow ? maxShown : shown;
            int rows = (slots + perRow - 1) / perRow;
            float rowHeight = Cell + NameHeight + BarHeight + Gap;
            float height = Pad * 2f + rows * rowHeight - Gap;

            Rect panel = new Rect(0f, pane.PaneTopY - height, width, height);
            Widgets.DrawWindowBackground(panel);

            // 접기 버튼: 패널 오른쪽 위 바깥(지도 쪽)에 붙여 칸을 가리지 않게
            Rect minBtn = new Rect(panel.xMax - 22f, panel.y - TabHeight, 22f, TabHeight);
            Text.Font = GameFont.Tiny;
            if (Widgets.ButtonText(minBtn, "−"))
            {
                minimized = true;
                dragging = false;
            }
            TooltipHandler.TipRegion(minBtn, "CG_GridMinimize".Translate());

            TextAnchor prevAnchor = Text.Anchor;
            Color prevColor = GUI.color;

            cellRects.Clear();
            for (int i = 0; i < shown; i++)
            {
                Rect cell = CellRect(panel, i, perRow, rowHeight);
                cellRects.Add(cell);
                DrawCell(cell, things[i]);
            }
            if (overflow)
            {
                Rect cell = CellRect(panel, shown, perRow, rowHeight);
                Widgets.DrawBoxSolid(cell, new Color(1f, 1f, 1f, 0.06f));
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(cell, "+" + (things.Count - shown));
            }

            Text.Font = prevFont;
            Text.Anchor = prevAnchor;
            GUI.color = prevColor;

            HandleMouse(panel);
            return true;
        }

        // 클릭·드래그를 직접 처리한다. 패널 위 MouseDown 은 전부 써서(우클릭 포함) 지도 선택·명령으로 새지 않게 한다.
        // 지도 드래그 박스는 Selector 가 MouseDown 에서 시작하므로, 그걸 여기서 먹으면 끄는 동안 지도 쪽은 아무것도 안 한다
        static void HandleMouse(Rect panel)
        {
            Event e = Event.current;
            Vector2 mouse = e.mousePosition;
            bool isBox = dragging && (mouse - dragStart).magnitude > DragThreshold;
            Rect box = Rect.MinMaxRect(Mathf.Min(dragStart.x, mouse.x), Mathf.Min(dragStart.y, mouse.y),
                                       Mathf.Max(dragStart.x, mouse.x), Mathf.Max(dragStart.y, mouse.y));

            if (isBox && e.type == EventType.Repaint)
            {
                Color prev = GUI.color;
                Widgets.DrawBoxSolid(box, new Color(BoxColor.r, BoxColor.g, BoxColor.b, 0.12f));
                GUI.color = BoxColor;
                Widgets.DrawBox(box, 1);
                // 놓으면 고를 칸을 미리 보여 준다
                for (int i = 0; i < cellRects.Count; i++)
                    if (cellRects[i].Overlaps(box)) Widgets.DrawBox(cellRects[i], 2);
                GUI.color = prev;
            }

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (!Mouse.IsOver(panel)) break;
                    if (e.button == 0)
                    {
                        dragging = true;
                        dragStart = mouse;
                    }
                    e.Use();
                    break;
                case EventType.MouseDrag:
                    if (dragging) e.Use();
                    break;
                case EventType.MouseUp:
                    if (!dragging || e.button != 0) break;
                    dragging = false;
                    e.Use();
                    if (!isBox)
                    {
                        for (int i = 0; i < cellRects.Count; i++)
                            if (cellRects[i].Contains(dragStart) && cellRects[i].Contains(mouse))
                            {
                                OnClick(things[i]);
                                break;
                            }
                        break;
                    }
                    List<Thing> hit = new List<Thing>();
                    for (int i = 0; i < cellRects.Count; i++)
                        if (cellRects[i].Overlaps(box)) hit.Add(things[i]);
                    if (hit.Count > 0) OnBox(hit, e.shift || e.control || e.command); // Ctrl/Cmd+드래그도 빼기(정착민 바·지도와 같은 키)
                    break;
            }
        }

        static void OnBox(List<Thing> hit, bool remove)
        {
            Selector sel = Find.Selector;
            if (remove)
            {
                foreach (Thing t in hit) sel.Deselect(t);
                return;
            }
            sel.ClearSelection();
            foreach (Thing t in hit) sel.Select(t, playSound: false, forceDesignatorDeselect: false);
        }

        static Rect CellRect(Rect panel, int index, int perRow, float rowHeight)
        {
            int r = index / perRow, c = index % perRow;
            return new Rect(panel.x + Pad + c * (Cell + Gap), panel.y + Pad + r * rowHeight, Cell, Cell + NameHeight + BarHeight);
        }

        static void DrawCell(Rect cell, Thing t)
        {
            Rect icon = new Rect(cell.x, cell.y, Cell, Cell);
            Pawn p = t as Pawn;
            bool drafted = p != null && p.Drafted;

            Widgets.DrawBoxSolid(icon, new Color(1f, 1f, 1f, 0.06f));
            if (Mouse.IsOver(cell))
            {
                Widgets.DrawBoxSolid(icon, new Color(1f, 1f, 1f, 0.12f));
                // 정착민 바에 마우스를 올렸을 때처럼 지도에서 그 대상 위에 화살표·원을 띄운다
                TargetHighlighter.Highlight(t, arrow: true, colonistBar: false, circleOverlay: true);
            }

            // 기분: 정착민 바처럼 칸 바닥부터 기분만큼 배경을 채우고, 정신붕괴 위험이면 그 단계 색을 옅게 덮는다
            if (p != null && p.needs?.mood != null && !p.Dead)
            {
                Rect mood = icon.ContractedBy(1f);
                float h = mood.height * Mathf.Clamp01(p.needs.mood.CurLevelPercentage);
                mood.yMin = mood.yMax - h;
                GUI.DrawTexture(mood, MoodBGTex);
                if (Prefs.VisibleMood && !p.Downed && p.mindState?.mentalBreaker != null && p.mindState.mentalBreaker.CanDoRandomMentalBreaks)
                {
                    MoodThreshold th = MoodThresholdExtensions.CurrentMoodThresholdFor(p);
                    if (th != MoodThreshold.None)
                        Widgets.DrawBoxSolid(icon, th.GetColor().ToTransparent(th < MoodThreshold.Major ? 0.15f : 0.22f));
                }
            }

            if (p != null && p.RaceProps.Humanlike)
            {
                GUI.color = p.Downed ? new Color(1f, 1f, 1f, 0.5f) : Color.white;
                GUI.DrawTexture(icon, PortraitsCache.Get(p, PortraitSize, Rot4.South, PortraitOffset, PortraitZoom));
                GUI.color = Color.white;
            }
            else
            {
                // 동물·메카·물건은 바닐라 아이콘 (초상화 카메라가 몸집에 안 맞는다).
                // 바닐라 ThingIcon 은 아이콘이 없는 것(GetIconFor 가 null·BadTex)이면 아무것도 안 그려 빈 칸이 된다 → 이름 글자로 대신
                Texture tex = Widgets.GetIconFor(t, icon.size, null, false, out _, out _, out _, out _, out _);
                if (tex != null && tex != BaseContent.BadTex)
                    Widgets.ThingIcon(icon.ContractedBy(3f), t);
                else
                {
                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(icon.ContractedBy(2f), t.def.LabelCap);
                }
            }

            if (drafted)
            {
                // 칸 사이 틈(3px)에 바깥 테두리를 그려 안쪽(기분·초상화)은 그대로 보이게
                GUI.color = DraftMark.Border;
                Widgets.DrawBox(icon.ExpandedBy(1f), 2);
                GUI.DrawTexture(new Rect(icon.xMax - 15f, icon.y + 1f, 14f, 14f), TexCommand.Draft);
                GUI.color = Color.white;
            }

            float pct = HealthPercent(t);
            if (pct >= 0f)
            {
                // 스타크래프트처럼 초록 → 노랑 → 빨강
                Color col = pct > 0.66f ? new Color(0.3f, 0.85f, 0.3f) : pct > 0.33f ? new Color(0.95f, 0.8f, 0.2f) : LowColor;
                DrawBar(new Rect(cell.x, icon.yMax + NameHeight + 1f, Cell, OneBar), pct, col);
            }

            // 허기(사람·동물) 또는 전력(메카). 굶주림·전력 부족이면 빨강
            if (p != null && p.needs != null)
            {
                Rect bar2 = new Rect(cell.x, icon.yMax + NameHeight + 1f + OneBar + 1f, Cell, OneBar);
                Need_MechEnergy energy = p.needs.energy;
                Need_Food food = p.needs.food;
                if (energy != null)
                    DrawBar(bar2, energy.CurLevelPercentage, energy.IsLowEnergySelfShutdown || energy.CurLevelPercentage < 0.15f ? LowColor : EnergyColor);
                else if (food != null)
                    DrawBar(bar2, food.CurLevelPercentage, food.CurCategory >= HungerCategory.UrgentlyHungry ? LowColor : FoodColor);

                // 수면(사람·동물, 메카는 없음). 매우 피곤·탈진이면 빨강
                Need_Rest rest = p.needs.rest;
                if (rest != null)
                {
                    Rect bar3 = new Rect(cell.x, bar2.yMax + 1f, Cell, OneBar);
                    DrawBar(bar3, rest.CurLevelPercentage, rest.CurCategory >= RestCategory.VeryTired ? LowColor : RestColor);
                }
            }

            // 이름 있는 폰(사람·메카·이름 붙인 동물)만. 건물·물건은 같은 종류면 이름도 같아 의미가 없다
            if (p != null && p.Name != null)
            {
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = drafted ? DraftMark.Border : new Color(0.85f, 0.85f, 0.85f);
                Rect nameRect = new Rect(cell.x - 1f, icon.yMax, Cell + 2f, NameHeight);
                Widgets.Label(nameRect, p.LabelShort.Truncate(nameRect.width));
                GUI.color = Color.white;
            }

            string tip = t.LabelCap.ToString();
            if (drafted) tip += " (" + "CG_Drafted".Translate() + ")";
            TooltipHandler.TipRegion(icon, tip + "\n\n" + "CG_GridTip".Translate());
        }

        static void DrawBar(Rect bar, float pct, Color col)
        {
            Widgets.DrawBoxSolid(bar, new Color(0f, 0f, 0f, 0.6f));
            bar.width *= Mathf.Clamp01(pct);
            Widgets.DrawBoxSolid(bar, col);
        }

        static float HealthPercent(Thing t)
        {
            if (t is Pawn p) return p.health?.summaryHealth?.SummaryHealthPercent ?? -1f;
            if (t.def.useHitPoints && t.MaxHitPoints > 0) return (float)t.HitPoints / t.MaxHitPoints;
            return -1f;
        }

        static void OnClick(Thing t)
        {
            Selector sel = Find.Selector;
            if (Event.current.shift)
            {
                sel.Deselect(t);
                return;
            }
            // Mac 은 Ctrl 클릭이 우클릭으로 잡히기도 해 Cmd 도 받는다 (부대 키와 같은 이유)
            if (Event.current.control || Event.current.command)
            {
                List<object> keep = new List<object>();
                foreach (object o in sel.SelectedObjects)
                    if (o is Thing other && other.def == t.def) keep.Add(o);
                sel.ClearSelection();
                foreach (object o in keep) sel.Select(o, playSound: false, forceDesignatorDeselect: false);
                return;
            }
            sel.ClearSelection();
            sel.Select(t, playSound: false, forceDesignatorDeselect: false);
        }
    }

    // 소집된 정착민은 바닐라 바에서 초상화 밑 작은 칼 아이콘뿐이라 잘 안 보인다 → 칸 전체에 빨간 테두리와 옅은 붉은 빛
    [HarmonyPatch(typeof(ColonistBarColonistDrawer), nameof(ColonistBarColonistDrawer.DrawColonist))]
    public static class Patch_ColonistBar_DraftMark
    {
        // Prefix: 바닐라보다 먼저 그려 이름표가 테두리 위에 오게 한다(Postfix 면 아래 테두리가 이름을 가림, 2026-11-25 사용자)
        public static void Prefix(Rect rect, Pawn colonist)
        {
            if (colonist == null || colonist.Dead || !colonist.Drafted) return;
            // 칸 안을 붉게 덮으면 기분 배경·초상화가 가려진다(2026-11-24 사용자) → 칸 바깥에 빨간 테두리만
            Color prev = GUI.color;
            int thick = rect.width <= 30f ? 2 : 3;
            GUI.color = DraftMark.Border;
            Widgets.DrawBox(rect.ExpandedBy(thick), thick);
            GUI.color = prev;
        }
    }

    // Ctrl/Cmd+드래그 = 박스 안을 선택에서 빼기(정착민 바 초상화·지도 공통, 2026-11-25 사용자). 빼기 모드면 드래그 박스를 빨갛게 그린다.
    // Shift+드래그는 바닐라대로 추가. 용도: 부대를 부른 뒤 아기 안은 애·기분 나쁜 애를 소집에서 빼기.
    // 박스에 정착민 바 초상화가 걸리면 그것만, 아니면 지도 위 물건(바닐라 드래그 선택과 같은 후보)에서 뺀다.
    // (~같은 날 기각: Shift 토글 "전부 선택돼 있으면 빼기", Shift 항상 빼기, Shift 시작 칸으로 가르기 — 쓰기 애매.)
    public static class DragRemove
    {
        public static bool Held
        {
            get
            {
                Event e = Event.current;
                if (e != null && (e.control || e.command)) return true;
                return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)
                    || Input.GetKey(KeyCode.LeftCommand) || Input.GetKey(KeyCode.RightCommand);
            }
        }
    }

    // 바닐라는 MouseUp 에서 dragBox.active=false 로 바꾼 직후 이 함수를 부른다 — active 는 보지 않는다
    [HarmonyPatch(typeof(Selector), "SelectInsideDragBox")]
    public static class Patch_Selector_DragRemove
    {
        public static bool Prefix(Selector __instance)
        {
            if (!DragRemove.Held) return true;
            Rect rect = __instance.dragBox.ScreenRect;
            List<Thing> boxed = Find.ColonistBar?.MapColonistsOrCorpsesInScreenRect(rect);
            if (boxed == null || boxed.Count == 0)
                boxed = ThingSelectionUtility.MultiSelectableThingsInScreenRectDistinct(rect).ToList();
            for (int i = 0; i < boxed.Count; i++)
                if (__instance.IsSelected(boxed[i])) __instance.Deselect(boxed[i]);
            return false;
        }
    }

    [HarmonyPatch(typeof(DragBox), nameof(DragBox.DragBoxOnGUI))]
    public static class Patch_DragBox_RemoveModeColor
    {
        public static void Prefix(DragBox __instance, out Color __state)
        {
            __state = GUI.color;
            if (Find.Selector != null && Find.Selector.dragBox == __instance && DragRemove.Held)
                GUI.color = DraftMark.Border;
        }

        public static void Postfix(Color __state)
        {
            GUI.color = __state;
        }
    }
}
