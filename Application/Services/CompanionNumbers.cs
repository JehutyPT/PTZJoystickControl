using PtzJoystickControl.Core.Devices;

namespace PtzJoystickControl.Application.Services;

/// <summary>
/// Rules for each camera's <see cref="ICamera.CompanionNumber"/>, the number a Companion
/// request selects it by.
/// </summary>
public static class CompanionNumbers
{
    /// <summary>
    /// Gives every camera without a number one: its 1-based list position, or the lowest unused
    /// number when another camera already has that position. Existing numbers are never changed.
    /// </summary>
    public static void AssignMissing(IReadOnlyList<ICamera> cameras)
    {
        for (var i = 0; i < cameras.Count; i++)
        {
            if (cameras[i].CompanionNumber == null)
                cameras[i].CompanionNumber = NextFree(cameras, preferred: i + 1);
        }
    }

    /// <summary>
    /// <paramref name="preferred"/> if no camera has it, otherwise the lowest number no camera has.
    /// </summary>
    public static int NextFree(IEnumerable<ICamera> cameras, int preferred)
    {
        var used = cameras.Select(c => c.CompanionNumber).OfType<int>().ToHashSet();
        if (preferred >= 1 && !used.Contains(preferred))
            return preferred;

        var number = 1;
        while (used.Contains(number))
            number++;
        return number;
    }

    /// <summary>The camera other than <paramref name="camera"/> that already has <paramref name="number"/>, if any.</summary>
    public static ICamera? UsedByAnotherCamera(IEnumerable<ICamera> cameras, ICamera camera, int number) =>
        cameras.FirstOrDefault(c => c != camera && c.CompanionNumber == number);

    /// <summary>Numbers that more than one camera has, each with the cameras sharing it.</summary>
    public static IEnumerable<IGrouping<int, ICamera>> Duplicates(IEnumerable<ICamera> cameras) =>
        cameras
            .Where(c => c.CompanionNumber != null)
            .GroupBy(c => c.CompanionNumber!.Value)
            .Where(g => g.Count() > 1);
}
