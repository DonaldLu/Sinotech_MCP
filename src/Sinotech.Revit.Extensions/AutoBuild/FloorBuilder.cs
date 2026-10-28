// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Segment = Sinotech.Revit.Extensions.AutoBuild.WallRegionGeometry.Segment;

namespace Sinotech.Revit.Extensions.AutoBuild
{
    /// <summary>
    /// 每個房間建一塊樓板，邊界在牆與柱的內緣（房間邊界的「面層」）：
    /// 先在子交易中自動放置房間取得邊界，再撤銷這些暫時房間（專案原有的房間保留並一併建板）。
    /// 房間內有電梯井叉線或樓梯踏步的不建板。須在已開始的交易中呼叫
    /// </summary>
    internal class FloorBuilder
    {
        private const double MinArea = 1 / (0.3048 * 0.3048); // 1 m²
        public int Created { get; private set; }
        public int SkippedShafts { get; private set; }
        public int SkippedStairs { get; private set; }
        public int Failed { get; private set; }
        public List<string> Errors { get; } = new List<string>();

        public void Build(Document doc, Level level, FloorType type, List<Segment> cadLines)
        {
            var profiles = new List<List<CurveLoop>>();
            using (var temporary = new SubTransaction(doc))
            {
                temporary.Start();
                doc.Regenerate();
                doc.Create.NewRooms2(level);
                doc.Regenerate();
                var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
                var rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                    .OfType<Room>().Where(r => r.LevelId == level.Id && r.Area >= MinArea);
                foreach (var room in rooms)
                {
                    var loops = new List<CurveLoop>();
                    foreach (var boundary in room.GetBoundarySegments(options) ?? new List<IList<BoundarySegment>>())
                    {
                        try { loops.Add(CurveLoop.Create(boundary.Select(b => b.GetCurve()).ToList())); }
                        catch (Exception) { loops.Clear(); break; }
                    }
                    if (loops.Count > 0) profiles.Add(loops);
                    else Failed++;
                }
                // 撤銷暫時建立的房間；邊界曲線是獨立的幾何資料，撤銷後仍可使用
                temporary.RollBack();
            }
            foreach (var loops in profiles)
            {
                var outline = loops[0].SelectMany(c => c.Tessellate()).Select(p => new WallRegionGeometry.Point(p.X, p.Y)).ToList();
                var inside = cadLines.Where(l => Contains(outline, (l.A + l.B) * .5)).ToList();
                if (IsShaft(outline, inside)) { SkippedShafts++; continue; }
                if (IsStair(inside)) { SkippedStairs++; continue; }
                using (var step = new SubTransaction(doc))
                {
                    step.Start();
                    try
                    {
#if REVIT2022_OR_GREATER
                        Floor.Create(doc, loops, type.Id, level.Id);
#else
                        var outer = new CurveArray();
                        foreach (var curve in loops[0]) outer.Append(curve);
                        doc.Create.NewFloor(outer, type, level, false);
#endif
                        step.Commit();
                        Created++;
                    }
                    catch (Exception ex)
                    {
                        step.RollBack(); Failed++;
                        if (Errors.Count < 3) Errors.Add("樓板：" + ex.Message);
                    }
                }
            }
        }

        // 電梯井、管道間：房間內有兩條互相交叉、長度達房間對角線 60% 的斜線（打叉）
        private static bool IsShaft(List<WallRegionGeometry.Point> outline, List<Segment> inside)
        {
            double width = outline.Max(p => p.X) - outline.Min(p => p.X), height = outline.Max(p => p.Y) - outline.Min(p => p.Y);
            double diagonal = Math.Sqrt(width * width + height * height);
            var longLines = inside.Where(l => l.Length >= 0.6 * diagonal).ToList();
            for (int i = 0; i < longLines.Count; i++)
                for (int j = i + 1; j < longLines.Count; j++)
                    if (Intersects(longLines[i], longLines[j])) return true;
            return false;
        }

        // 樓梯：至少 6 條平行、長度相近的線，相鄰間距 20～35 cm 連續出現（踏步）
        private static bool IsStair(List<Segment> inside)
        {
            foreach (var group in inside.Where(l => l.Length > 60 / 30.48).GroupBy(l => Math.Round(Angle(l) / (Math.PI / 180))))
            {
                var lines = group.ToList();
                if (lines.Count < 6) continue;
                double angle = Angle(lines[0]);
                double nx = -Math.Sin(angle), ny = Math.Cos(angle);
                var offsets = lines.Select(l => ((l.A.X + l.B.X) / 2 * nx + (l.A.Y + l.B.Y) / 2 * ny)).OrderBy(o => o).ToList();
                int run = 1;
                for (int i = 1; i < offsets.Count; i++)
                {
                    double gap = (offsets[i] - offsets[i - 1]) * 30.48;
                    run = gap >= 20 && gap <= 35 ? run + 1 : gap < 1 ? run : 1;
                    if (run >= 6) return true;
                }
            }
            return false;
        }

        private static double Angle(Segment l)
        {
            double a = Math.Atan2(l.B.Y - l.A.Y, l.B.X - l.A.X);
            return a < 0 ? a + Math.PI : a >= Math.PI ? a - Math.PI : a;
        }

        private static bool Intersects(Segment a, Segment b)
        {
            double dx1 = a.B.X - a.A.X, dy1 = a.B.Y - a.A.Y, dx2 = b.B.X - b.A.X, dy2 = b.B.Y - b.A.Y;
            double det = dx1 * dy2 - dy1 * dx2;
            if (Math.Abs(det) < 1e-9 * a.Length * b.Length || Math.Abs(det) < 0.2 * a.Length * b.Length) return false;
            double t = ((b.A.X - a.A.X) * dy2 - (b.A.Y - a.A.Y) * dx2) / det, u = ((b.A.X - a.A.X) * dy1 - (b.A.Y - a.A.Y) * dx1) / det;
            return t > 0.1 && t < 0.9 && u > 0.1 && u < 0.9;
        }

        private static bool Contains(List<WallRegionGeometry.Point> p, WallRegionGeometry.Point q)
        {
            bool inside = false;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
                if ((p[i].Y > q.Y) != (p[j].Y > q.Y) && q.X < (p[j].X - p[i].X) * (q.Y - p[i].Y) / (p[j].Y - p[i].Y) + p[i].X)
                    inside = !inside;
            return inside;
        }
    }
}
