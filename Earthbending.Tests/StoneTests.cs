using Earthbending;
using static Program;

internal static class StoneTests {
    static UvRect[] StoneUvSix() {
        var r = new UvRect[StoneBox.Faces];
        for (int i = 0; i < r.Length; i++) r[i] = StoneUv.Slate(1f, 1f);
        return r;
    }

    internal static void Run() {
        // spec 3.4: the table's numbers
        Check("table wall", StoneTable.Of(StoneKind.Wall).Power == 3 && Near(StoneTable.Of(StoneKind.Wall).Mass, 400f));
        Check("table disc", StoneTable.Of(StoneKind.Disc).Power == 0 && Near(StoneTable.Of(StoneKind.Disc).Size.Y, 0.14f));
        // the chamfered block the Pillar, Cube and Wall are drawn with: 6 faces, 12 edge strips, 8 corners
        var box = StoneBox.Build(1f, 1f, 1f, 0.05f, StoneUvSix());   // six StoneUv.Slate rects
        Check("box bevelled", box.VertexCount == StoneBox.BevelledVertices && box.TriangleCount == StoneBox.BevelledTriangles);
    }
}
