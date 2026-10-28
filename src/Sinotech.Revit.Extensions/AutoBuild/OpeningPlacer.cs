// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Sinotech.Revit.Extensions.AutoBuild.Models;
using Kind = Sinotech.Revit.Extensions.AutoBuild.OpeningDetector.Kind;
using Segment = Sinotech.Revit.Extensions.AutoBuild.WallRegionGeometry.Segment;

namespace Sinotech.Revit.Extensions.AutoBuild
{
    /// <summary>
    /// 把 OpeningDetector 判斷出的門窗放到牆上：選用專案中寬度最接近的門窗類型（相差超過 10 mm 時複製一個等寬類型），
    /// 再依平面圖中族群自身的開門弧線調整開門方向與鉸鏈側；須在已開始的交易中呼叫
    /// </summary>
    internal class OpeningPlacer
    {
        private static readonly string[] DoubleWords = { "DOUBLE", "PAIR", "雙" };
        private static readonly string[] SlidingWords = { "SLIDING", "SLIDE", "推拉", "拉門", "滑" };
        private const double MatchTolerance = 10 / 304.8;

        private readonly Document _doc;
        private readonly Level _level;
        private readonly CadWallCreationResult _stats;
        private readonly List<FamilySymbol> _doors, _windows;

        public OpeningPlacer(Document doc, Level level, CadWallCreationResult stats)
        {
            _doc = doc; _level = level; _stats = stats;
            _doors = Symbols(BuiltInCategory.OST_Doors);
            _windows = Symbols(BuiltInCategory.OST_Windows);
        }

        public void Place(IEnumerable<OpeningDetector.Opening> openings, List<Tuple<Segment, Wall>> hosts, double? sillHeight)
        {
            var doors = new List<Tuple<FamilyInstance, OpeningDetector.Opening>>();
            foreach (var opening in openings)
            {
                var c = opening.Center;
                if (opening.Kind == Kind.Unknown)
                {
                    _stats.UnknownOpenings.Add($"({Math.Round(c.X * 304.8)}, {Math.Round(c.Y * 304.8)}) 寬 {Math.Round(opening.Width * 304.8)} mm");
                    continue;
                }
                var host = hosts.FirstOrDefault(h => OnSegment(h.Item1, c, opening.WallWidth / 2))?.Item2;
                bool isWindow = opening.Kind == Kind.Window;
                FamilySymbol symbol = host == null ? null : SymbolFor(opening.Kind, opening.Width);
                if (symbol == null) { _stats.OpeningFailures++; continue; }
                using (var step = new SubTransaction(_doc))
                {
                    step.Start();
                    try
                    {
                        if (!symbol.IsActive) symbol.Activate();
                        var instance = _doc.Create.NewFamilyInstance(new XYZ(c.X, c.Y, _level.Elevation), symbol, host, _level, StructuralType.NonStructural);
                        if (isWindow && sillHeight.HasValue)
                            instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.Set(sillHeight.Value / 304.8);
                        step.Commit();
                        Count(opening.Kind, symbol);
                        if (opening.Kind == Kind.Door || opening.Kind == Kind.DoubleDoor) doors.Add(Tuple.Create(instance, opening));
                    }
                    catch (Exception ex)
                    {
                        step.RollBack(); _stats.OpeningFailures++;
                        if (_stats.Errors.Count < 3) _stats.Errors.Add("門窗：" + ex.Message);
                    }
                }
            }
            Orient(doors);
        }

        // 依族群自己在平面圖畫的開門弧線，翻轉開門方向（facing）與鉸鏈側（hand）以符合 CAD
        private void Orient(List<Tuple<FamilyInstance, OpeningDetector.Opening>> doors)
        {
            if (doors.Count == 0) return;
            var view = _doc.ActiveView as ViewPlan;
            if (view == null || view.GenLevel?.Id != _level.Id)
                view = new FilteredElementCollector(_doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .FirstOrDefault(v => !v.IsTemplate && v.GenLevel?.Id == _level.Id);
            if (view == null) { _stats.OrientationNote = "找不到基準樓層的平面視圖，門的開啟方向未依 CAD 調整。"; return; }
            _doc.Regenerate();
            var options = new Options { View = view };
            int unresolved = 0;
            foreach (var pair in doors)
            {
                var instance = pair.Item1; var opening = pair.Item2;
                var arc = SwingArc(instance, options);
                if (arc == null) { unresolved++; continue; }
                var c = opening.Center;
                var center = new XYZ(c.X, c.Y, 0);
                var normal = new XYZ(opening.SwingSide.X, opening.SwingSide.Y, 0);
                var along = new XYZ(opening.End.X - opening.Start.X, opening.End.Y - opening.Start.Y, 0).Normalize();
                var mid = arc.Evaluate(0.5, true); mid = new XYZ(mid.X, mid.Y, 0);
                bool flipFacing = (mid - center).DotProduct(normal) < 0;
                bool flipHand = false;
                if (opening.Kind == Kind.Door)
                {
                    var hinge = new XYZ(opening.Hinge.X, opening.Hinge.Y, 0);
                    var familyHinge = new XYZ(arc.Center.X, arc.Center.Y, 0);
                    flipHand = (familyHinge - center).DotProduct(along) * (hinge - center).DotProduct(along) < 0;
                }
                if (flipFacing && instance.CanFlipFacing) instance.flipFacing();
                if (flipHand && instance.CanFlipHand) instance.flipHand();
            }
            if (unresolved > 0) _stats.OrientationNote = $"{unresolved} 樘門的族群在平面圖沒有開門弧線，開啟方向未依 CAD 調整，請檢查。";
        }

        // 族群在平面視圖中最大的圓弧即為開門弧線
        private static Arc SwingArc(FamilyInstance instance, Options options)
        {
            var arcs = new List<Arc>();
            Collect(instance.get_Geometry(options), arcs);
            return arcs.Where(a => a.IsBound).OrderByDescending(a => a.Radius).FirstOrDefault();
        }

        private static void Collect(GeometryElement geometry, List<Arc> arcs)
        {
            if (geometry == null) return;
            foreach (GeometryObject obj in geometry)
            {
                if (obj is GeometryInstance gi) Collect(gi.GetInstanceGeometry(), arcs);
                else if (obj is Arc arc) arcs.Add(arc);
            }
        }

        // 依門窗種類挑族群：雙開門用名稱含 Double／雙 的族群，推拉門用含 Sliding／推拉 的族群（沒有就用一般門），
        // 再選寬度最接近的類型；相差超過 10 mm 時複製一個等寬類型
        private FamilySymbol SymbolFor(Kind kind, double width)
        {
            List<FamilySymbol> pool;
            if (kind == Kind.Window) pool = _windows;
            else
            {
                var doubles = _doors.Where(s => Has(s, DoubleWords)).ToList();
                var sliding = _doors.Where(s => Has(s, SlidingWords)).ToList();
                var single = _doors.Except(doubles).Except(sliding).ToList();
                pool = kind == Kind.DoubleDoor ? doubles : kind == Kind.SlidingDoor ? sliding : single;
                if (pool.Count == 0) pool = single.Count > 0 ? single : _doors;
            }
            var candidates = pool.Where(s => WidthParameter(s) != null).ToList();
            if (candidates.Count == 0) return pool.FirstOrDefault();
            var best = candidates.OrderBy(s => Math.Abs(WidthParameter(s).AsDouble() - width)).ThenBy(s => s.Name).First();
            if (Math.Abs(WidthParameter(best).AsDouble() - width) <= MatchTolerance) return best;
            string name = $"{best.Name} {Math.Round(width * 304.8)}mm";
            var existing = best.Family.GetFamilySymbolIds().Select(id => _doc.GetElement(id)).OfType<FamilySymbol>().FirstOrDefault(s => s.Name == name);
            if (existing != null) return existing;
            var parameter = WidthParameter(best);
            if (parameter.IsReadOnly) return best;
            var copy = (FamilySymbol)best.Duplicate(name);
            WidthParameter(copy).Set(width);
            pool.Add(copy);
            (kind == Kind.Window ? _windows : _doors).Add(copy);
            _stats.CreatedOpeningTypes.Add($"{copy.FamilyName}：{copy.Name}");
            return copy;
        }

        private static Parameter WidthParameter(FamilySymbol symbol)
        {
            return symbol.get_Parameter(BuiltInParameter.DOOR_WIDTH) ?? symbol.get_Parameter(BuiltInParameter.WINDOW_WIDTH) ??
                symbol.get_Parameter(BuiltInParameter.FAMILY_WIDTH_PARAM) ?? symbol.LookupParameter("Width") ?? symbol.LookupParameter("寬度");
        }

        private List<FamilySymbol> Symbols(BuiltInCategory category)
        {
            return new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).OfCategory(category).Cast<FamilySymbol>().ToList();
        }

        private static bool Has(FamilySymbol symbol, string[] words)
        {
            string name = symbol.FamilyName + " " + symbol.Name;
            return words.Any(w => name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool OnSegment(Segment s, WallRegionGeometry.Point p, double tolerance)
        {
            double dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y, length = s.Length;
            double t = ((p.X - s.A.X) * dx + (p.Y - s.A.Y) * dy) / length;
            double offset = Math.Abs((p.X - s.A.X) * dy - (p.Y - s.A.Y) * dx) / length;
            return t > 0 && t < length && offset <= tolerance;
        }

        private void Count(Kind kind, FamilySymbol symbol)
        {
            if (kind == Kind.Door) _stats.DoorsPlaced++;
            else if (kind == Kind.DoubleDoor) _stats.DoubleDoorsPlaced++;
            else if (kind == Kind.SlidingDoor) _stats.SlidingDoorsPlaced++;
            else _stats.WindowsPlaced++;
            string key = $"{symbol.FamilyName}：{symbol.Name}";
            _stats.OpeningTypeCounts[key] = (_stats.OpeningTypeCounts.TryGetValue(key, out int n) ? n : 0) + 1;
        }
    }
}
