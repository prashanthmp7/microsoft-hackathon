using SecondHalfMind.Domain;

namespace SecondHalfMind.Agents.Vision;

/// <summary>What auto-eventing recovered from one ball track.</summary>
public sealed record BallKick(double StartX, double StartY, double EndX, double EndY, double DistanceM, double SpeedKmh, int FramesUsed);

/// <summary>
/// Turns raw ball positions into a kick: where it started, where it stopped, how far it travelled
/// and how fast. It uses only the frames (never the tagged event), handles dropped frames by using
/// each sample's timestamp, and fits speed by least squares over the in-flight frames so
/// per-frame noise averages out.
/// </summary>
public static class BallTrackAnalyzer
{
    private const int EndpointFrames = 5;
    private const double MovementMarginM = 0.75;
    private const int MinFlightFrames = 3;

    public static BallKick? Analyze(BallTrack track)
    {
        var samples = track.Samples.OrderBy(s => s.T).ToList();
        if (samples.Count < EndpointFrames * 2 + MinFlightFrames)
        {
            return null;
        }

        // The ball is still before the strike and after it arrives: median of a few frames at
        // each end gives noise-resistant start and end positions.
        var startX = Median(samples.Take(EndpointFrames).Select(s => s.X));
        var startY = Median(samples.Take(EndpointFrames).Select(s => s.Y));
        var endX = Median(samples.TakeLast(EndpointFrames).Select(s => s.X));
        var endY = Median(samples.TakeLast(EndpointFrames).Select(s => s.Y));

        var dx = endX - startX;
        var dy = endY - startY;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance < MovementMarginM * 2 + 0.5)
        {
            return null;
        }

        var ux = dx / distance;
        var uy = dy / distance;

        // Progress along the line of travel; keep only frames clearly between the two ends.
        var flight = samples
            .Select(s => (s.T, Along: (s.X - startX) * ux + (s.Y - startY) * uy))
            .Where(p => p.Along > MovementMarginM && p.Along < distance - MovementMarginM)
            .ToList();
        if (flight.Count < MinFlightFrames)
        {
            return null;
        }

        var speedMs = Slope(flight);
        if (speedMs <= 0)
        {
            return null;
        }

        return new BallKick(startX, startY, endX, endY, distance, speedMs * 3.6, flight.Count);
    }

    private static double Slope(IReadOnlyList<(double T, double Along)> points)
    {
        var meanT = points.Average(p => p.T);
        var meanS = points.Average(p => p.Along);
        var num = points.Sum(p => (p.T - meanT) * (p.Along - meanS));
        var den = points.Sum(p => (p.T - meanT) * (p.T - meanT));
        return den <= 0 ? 0 : num / den;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
