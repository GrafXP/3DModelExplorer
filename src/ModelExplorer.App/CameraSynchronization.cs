using System.Numerics;
using System.Windows.Media.Media3D;
using Camera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;

namespace ModelExplorer.App;

public static class CameraSynchronization
{
    /// <summary>Preserves the same world scale, orientation and pan in each pane.</summary>
    public static void Copy(Camera source, Camera target, Vector3 targetOffset)
    {
        target.Position = source.Position + new Vector3D(targetOffset.X, targetOffset.Y, targetOffset.Z);
        target.LookDirection = source.LookDirection;
        target.UpDirection = source.UpDirection;
        target.FieldOfView = source.FieldOfView;
    }
}
