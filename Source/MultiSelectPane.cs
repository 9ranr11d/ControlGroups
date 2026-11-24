using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ControlGroups
{
    // 여러 명을 고르면 바닐라 검사 창엔 제목("인간 x9")만 있고 비어 있다. 그 빈 자리에 허가 구역·식량·복장·약물·독서 정책 드롭다운을 그린다.
    // RimHUD 는 단일 폰 선택일 때만 창을 직접 그리고(prefix false) 크기를 바꾼다. postfix 는 그때도 불리므로 NumSelected > 1 가드가 필수
    [HarmonyPatch(typeof(InspectPaneUtility), nameof(InspectPaneUtility.InspectPaneOnGUI))]
    public static class Patch_InspectPaneOnGUI
    {
        // OnGUI 는 매 프레임이라 목록을 재사용한다
        static readonly List<Pawn> areaTargets = new List<Pawn>();
        static readonly List<Pawn> foodTargets = new List<Pawn>();
        static readonly List<Pawn> outfitTargets = new List<Pawn>();
        static readonly List<Pawn> drugTargets = new List<Pawn>();
        static readonly List<Pawn> readingTargets = new List<Pawn>();

        // 검사 창은 432x165(PaneSizeFor)라 안쪽 높이가 약 145, 제목 아래 36 을 빼면 109 뿐이다.
        // 줄 5개를 한 열로 쌓으면 5*26+4*2=138 로 넘치므로 두 열(왼쪽 3줄 82, 오른쪽 2줄 54)로 나눈다
        const float RowHeight = 26f;
        const float RowGap = 2f;
        const float ColumnGap = 10f;
        const float LabelGap = 8f;
        const float ButtonMaxWidth = 220f;

        // 다섯 라벨 중 최대 폭 — 한국어도 안 잘리게 실제 폰트로 잰다. 처음 한 번만 (언어를 바꾸면 재시작이 필요한 게 보통)
        static float labelWidth = -1f;
        static readonly string[] labelKeys = { "CG_AreaLabel", "CG_FoodLabel", "CG_OutfitLabel", "CG_DrugLabel", "CG_ReadingLabel" };

        public static void Postfix(Rect inRect, IInspectPane pane)
        {
            if (Current.ProgramState != ProgramState.Playing || WorldRendererUtility.WorldSelected) return;
            Selector sel = Find.Selector;
            if (sel == null || sel.NumSelected <= 1) return;

            areaTargets.Clear();
            foodTargets.Clear();
            outfitTargets.Clear();
            drugTargets.Clear();
            readingTargets.Clear();
            List<object> selected = sel.SelectedObjects;
            for (int i = 0; i < selected.Count; i++)
            {
                if (!(selected[i] is Pawn p)) continue;
                if (IsAreaTarget(p)) areaTargets.Add(p);
                if (IsFoodTarget(p)) foodTargets.Add(p);
                if (IsOutfitTarget(p)) outfitTargets.Add(p);
                if (IsDrugTarget(p)) drugTargets.Add(p);
                if (IsReadingTarget(p)) readingTargets.Add(p);
            }
            if (areaTargets.Count == 0 && foodTargets.Count == 0 && outfitTargets.Count == 0
                && drugTargets.Count == 0 && readingTargets.Count == 0) return;

            // 바닐라 InspectPaneOnGUI 와 같은 rect(inRect 에서 12 줄이고 위 -4), 제목(Medium)이 그 안 0~약 30 이라 36 아래부터 시작
            Rect inner = inRect.ContractedBy(12f);
            inner.yMin -= 4f;
            float top = inner.y + 36f;

            GameFont prevFont = Text.Font;
            TextAnchor prevAnchor = Text.Anchor;
            Text.Font = GameFont.Small;

            if (labelWidth < 0f)
            {
                labelWidth = 0f;
                foreach (string key in labelKeys)
                    labelWidth = Mathf.Max(labelWidth, Text.CalcSize(key.Translate()).x + 4f);
            }
            float colWidth = (inner.width - ColumnGap) / 2f;
            float buttonWidth = Mathf.Min(ButtonMaxWidth, colWidth - labelWidth - LabelGap);

            // 대상 없는 줄은 빼고 위로 당긴다. 왼쪽 열 = 허가 구역·식량·복장, 오른쪽 열 = 약물·독서
            float yLeft = top, yRight = top;
            float xLeft = inner.x, xRight = inner.x + colWidth + ColumnGap;
            if (areaTargets.Count > 0)
            {
                DrawRow(xLeft, yLeft, buttonWidth, "CG_AreaLabel", AreaText(), AreaMenu);
                yLeft += RowHeight + RowGap;
            }
            if (foodTargets.Count > 0)
            {
                DrawRow(xLeft, yLeft, buttonWidth, "CG_FoodLabel", FoodText(), FoodMenu);
                yLeft += RowHeight + RowGap;
            }
            if (outfitTargets.Count > 0)
                DrawRow(xLeft, yLeft, buttonWidth, "CG_OutfitLabel", OutfitText(), OutfitMenu);
            if (drugTargets.Count > 0)
            {
                DrawRow(xRight, yRight, buttonWidth, "CG_DrugLabel", DrugText(), DrugMenu);
                yRight += RowHeight + RowGap;
            }
            if (readingTargets.Count > 0)
                DrawRow(xRight, yRight, buttonWidth, "CG_ReadingLabel", ReadingText(), ReadingMenu);

            Text.Font = prevFont;
            Text.Anchor = prevAnchor;
        }

        static void DrawRow(float x, float y, float buttonWidth, string labelKey, string value, System.Action openMenu)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(x, y, labelWidth, RowHeight), labelKey.Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            // 열이 좁아 정책 이름이 길면 버튼 밖으로 나가므로 자른다 (▼ 자리 약 20)
            if (Widgets.ButtonText(new Rect(x + labelWidth + LabelGap, y, buttonWidth, RowHeight), value.Truncate(buttonWidth - 24f) + " ▼"))
                openMenu();
        }

        // 바닐라 배정 탭 열(PawnColumnWorker_AllowedArea)과 같은 조건
        static bool IsAreaTarget(Pawn p)
        {
            return p.Faction == Faction.OfPlayer
                && p.playerSettings != null
                && p.playerSettings.SupportsAllowedAreas
                && (!p.IsMutant || p.mutant.Def.respectsAllowedArea)
                && (!p.RaceProps.IsMechanoid || p.GetOverseer() != null);
        }

        // 바닐라 PawnColumnWorker_FoodRestriction 은 foodRestriction != null 만 보지만, 정책을 못 바꾸는 대상(Configurable 아님)은 뺀다
        static bool IsFoodTarget(Pawn p)
        {
            return p.foodRestriction != null && p.foodRestriction.Configurable && p.foodRestriction.CurrentFoodPolicy != null;
        }

        // 구역은 맵 소속이라 첫 폰과 같은 맵의 폰만 비교·적용한다
        static string AreaText()
        {
            Map map = areaTargets[0].MapHeld;
            Area cur = areaTargets[0].playerSettings.AreaRestrictionInPawnCurrentMap;
            for (int i = 1; i < areaTargets.Count; i++)
            {
                Pawn p = areaTargets[i];
                if (p.MapHeld == map && p.playerSettings.AreaRestrictionInPawnCurrentMap != cur)
                    return "CG_Multiple".Translate();
            }
            return AreaUtility.AreaAllowedLabel_Area(cur);
        }

        static void AreaMenu()
        {
            Map map = areaTargets[0].MapHeld;
            if (map == null) return;
            // 클릭 시점에만 복사 — 메뉴가 열린 뒤 선택이 바뀌어도 열 때의 대상에 건다
            List<Pawn> here = new List<Pawn>();
            foreach (Pawn p in areaTargets) if (p.MapHeld == map) here.Add(p);

            List<FloatMenuOption> opts = new List<FloatMenuOption>
            {
                new FloatMenuOption("NoAreaAllowed".Translate(), () => SetArea(here, null))
            };
            // 바닐라 AreaAllowedGUI.DoAllowedAreaSelectors 와 같은 목록
            foreach (Area a in map.areaManager.AllAreas)
            {
                if (!a.AssignableAsAllowed()) continue;
                Area area = a;
                opts.Add(new FloatMenuOption(area.Label, () => SetArea(here, area)));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        static void SetArea(List<Pawn> pawns, Area area)
        {
            foreach (Pawn p in pawns)
            {
                if (p.playerSettings == null) continue;
                if (area != null && p.MapHeld != area.Map) continue;
                // 바닐라 배정 탭과 같은 세터 — 다른 모드(AreaExclusion)가 이 세터에 걸려 있다
                p.playerSettings.AreaRestrictionInPawnCurrentMap = area;
            }
        }

        static string FoodText()
        {
            FoodPolicy cur = foodTargets[0].foodRestriction.CurrentFoodPolicy;
            for (int i = 1; i < foodTargets.Count; i++)
                if (foodTargets[i].foodRestriction.CurrentFoodPolicy != cur)
                    return "CG_Multiple".Translate();
            return cur.label;
        }

        static void FoodMenu()
        {
            List<Pawn> targets = new List<Pawn>(foodTargets);
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            foreach (FoodPolicy policy in Current.Game.foodRestrictionDatabase.AllFoodRestrictions)
            {
                FoodPolicy fp = policy;
                opts.Add(new FloatMenuOption(fp.label, () =>
                {
                    foreach (Pawn p in targets)
                        if (p.foodRestriction != null && p.foodRestriction.Configurable)
                            p.foodRestriction.CurrentFoodPolicy = fp;
                }));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        // 바닐라 PawnColumnWorker_Outfit: pawn.outfits != null 이고, 퀘스트 체류자(IsQuestLodger)는 "변경 불가"라 뺀다
        // 복장·약물·독서 트래커는 손님·적 폰에도 있어, 섞어 골랐을 때 남의 정책을 바꾸지 않게 우리 소속만
        static bool IsOutfitTarget(Pawn p)
        {
            return p.Faction == Faction.OfPlayer && p.outfits != null && p.outfits.CurrentApparelPolicy != null && !p.IsQuestLodger();
        }

        // 바닐라 PawnColumnWorker_DrugPolicy: pawn.drugs != null (DoAssignDrugPolicyButtons 는 CurrentPolicy == null, 즉 정책 비활성 변이체도 그리지 않는다)
        static bool IsDrugTarget(Pawn p)
        {
            return p.Faction == Faction.OfPlayer && p.drugs != null && p.drugs.CurrentPolicy != null;
        }

        // 바닐라 PawnColumnWorker_Reading: pawn.reading != null (CurrentPolicy null 은 위와 같은 변이체 경우)
        static bool IsReadingTarget(Pawn p)
        {
            return p.Faction == Faction.OfPlayer && p.reading != null && p.reading.CurrentPolicy != null;
        }

        static string OutfitText()
        {
            ApparelPolicy cur = outfitTargets[0].outfits.CurrentApparelPolicy;
            for (int i = 1; i < outfitTargets.Count; i++)
                if (outfitTargets[i].outfits.CurrentApparelPolicy != cur)
                    return "CG_Multiple".Translate();
            return cur.label;
        }

        static void OutfitMenu()
        {
            List<Pawn> targets = new List<Pawn>(outfitTargets);
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            foreach (ApparelPolicy policy in Current.Game.outfitDatabase.AllOutfits)
            {
                ApparelPolicy ap = policy;
                opts.Add(new FloatMenuOption(ap.label, () =>
                {
                    foreach (Pawn p in targets)
                        if (p.outfits != null && !p.IsQuestLodger())
                            p.outfits.CurrentApparelPolicy = ap;
                }));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        static string DrugText()
        {
            DrugPolicy cur = drugTargets[0].drugs.CurrentPolicy;
            for (int i = 1; i < drugTargets.Count; i++)
                if (drugTargets[i].drugs.CurrentPolicy != cur)
                    return "CG_Multiple".Translate();
            return cur.label;
        }

        static void DrugMenu()
        {
            List<Pawn> targets = new List<Pawn>(drugTargets);
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            foreach (DrugPolicy policy in Current.Game.drugPolicyDatabase.AllPolicies)
            {
                DrugPolicy dp = policy;
                opts.Add(new FloatMenuOption(dp.label, () =>
                {
                    foreach (Pawn p in targets)
                        if (p.drugs != null)
                            p.drugs.CurrentPolicy = dp;
                }));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        static string ReadingText()
        {
            ReadingPolicy cur = readingTargets[0].reading.CurrentPolicy;
            for (int i = 1; i < readingTargets.Count; i++)
                if (readingTargets[i].reading.CurrentPolicy != cur)
                    return "CG_Multiple".Translate();
            return cur.label;
        }

        static void ReadingMenu()
        {
            List<Pawn> targets = new List<Pawn>(readingTargets);
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            foreach (ReadingPolicy policy in Current.Game.readingPolicyDatabase.AllReadingPolicies)
            {
                ReadingPolicy rp = policy;
                opts.Add(new FloatMenuOption(rp.label, () =>
                {
                    foreach (Pawn p in targets)
                        if (p.reading != null)
                            p.reading.CurrentPolicy = rp;
                }));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }
    }
}
