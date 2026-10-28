namespace MiniDisplay.Core;

public enum ResizeHandle { None, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }
public readonly record struct ResizeAnchor(ResizeHandle Handle, double X, double Y);

public static class LayerResize
{
    public const double MarkerSize = 4;
    public static bool Supported(PageLayer layer) => layer.Type is LayerType.Image or LayerType.Gif or LayerType.Artwork;

    public static ResizeAnchor[] Anchors(PageLayer layer)
    {
        if (!Supported(layer)) return [];
        double left = layer.X, top = layer.Y, right = Math.Min(240, left + layer.Width), bottom = Math.Min(240, top + layer.Height);
        ResizeAnchor Point(ResizeHandle handle, double x, double y) => new(handle, Math.Clamp(x, MarkerSize / 2, 240 - MarkerSize / 2), Math.Clamp(y, MarkerSize / 2, 240 - MarkerSize / 2));
        return [Point(ResizeHandle.TopLeft,left,top), Point(ResizeHandle.Top,(left+right)/2,top), Point(ResizeHandle.TopRight,right,top),
            Point(ResizeHandle.Right,right,(top+bottom)/2), Point(ResizeHandle.BottomRight,right,bottom), Point(ResizeHandle.Bottom,(left+right)/2,bottom),
            Point(ResizeHandle.BottomLeft,left,bottom), Point(ResizeHandle.Left,left,(top+bottom)/2)];
    }

    public static ResizeHandle HitTest(PageLayer layer, double x, double y, double radius)
    {
        double best = radius * radius; var hit = ResizeHandle.None;
        foreach (var anchor in Anchors(layer))
        {
            double distance = (x-anchor.X)*(x-anchor.X) + (y-anchor.Y)*(y-anchor.Y);
            if (distance <= best) { best = distance; hit = anchor.Handle; }
        }
        return hit;
    }

    public static PageLayer Apply(PageLayer original, ResizeHandle handle, double deltaX, double deltaY, bool keepAspect)
    {
        if (!Supported(original) || handle == ResizeHandle.None || !double.IsFinite(deltaX) || !double.IsFinite(deltaY)) return original;
        bool left = handle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft;
        bool right = handle is ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight;
        bool top = handle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight;
        bool bottom = handle is ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight;
        int x = original.X, y = original.Y, farX = Math.Min(240,x+original.Width), farY = Math.Min(240,y+original.Height);
        int width = farX-x, height = farY-y;
        if (keepAspect && (left || right) && (top || bottom))
        {
            double changeX = (left ? -deltaX : deltaX) / width, changeY = (top ? -deltaY : deltaY) / height;
            double scale = 1 + (Math.Abs(changeX) >= Math.Abs(changeY) ? changeX : changeY);
            int maxWidth = left ? farX : 240-x, maxHeight = top ? farY : 240-y;
            scale = Math.Clamp(scale, Math.Max(1d/width,1d/height), Math.Min((double)maxWidth/width,(double)maxHeight/height));
            int resizedWidth = Math.Clamp((int)Math.Round(width*scale),1,maxWidth), resizedHeight = Math.Clamp((int)Math.Round(height*scale),1,maxHeight);
            return original with { X = left ? farX-resizedWidth : x, Y = top ? farY-resizedHeight : y, Width = resizedWidth, Height = resizedHeight };
        }
        if (left) x = (int)Math.Round(Math.Clamp(x+deltaX,0,farX-1));
        if (right) farX = (int)Math.Round(Math.Clamp(farX+deltaX,x+1,240));
        if (top) y = (int)Math.Round(Math.Clamp(y+deltaY,0,farY-1));
        if (bottom) farY = (int)Math.Round(Math.Clamp(farY+deltaY,y+1,240));
        return original with { X=x, Y=y, Width=farX-x, Height=farY-y };
    }
}
