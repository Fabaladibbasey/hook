using NetTopologySuite.Geometries;

namespace Hook.Features.Geocoding.Models;

public readonly record struct Location(double Latitude, double Longitude)
{
    public Point ToPoint()
    {
        var point = new Point(Longitude, Latitude) { SRID = 4326 };
        return point;
    }

    public static Location FromPoint(Point point) => new(point.Y, point.X);

    public bool IsValid =>
        Latitude is >= -90 and <= 90 &&
        Longitude is >= -180 and <= 180;
}
