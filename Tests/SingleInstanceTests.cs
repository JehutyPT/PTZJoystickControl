using PtzJoystickControl.Application;
using Xunit;

namespace PtzJoystickControl.Tests;

public class SingleInstanceTests
{
    [Fact]
    public void MutexName_HasNoPathSeparators()
    {
        // .NET on Unix throws IOException for a '/' in a mutex name; on Windows '\' is a namespace separator.
        Assert.DoesNotContain('/', SingleInstance.MutexName);
        Assert.DoesNotContain('\\', SingleInstance.MutexName);
    }

    [Fact]
    public void CreateMutex_WithTheAppsName_DoesNotThrowOnThisOs()
    {
        // Construct only: acquiring it would clash with a copy of the app running on this machine.
        using var mutex = SingleInstance.CreateMutex();
    }

    [Fact]
    public void CreateMutex_WhileHeldElsewhere_RefusesSecondAcquirer()
    {
        // A unique name so a running copy of the app cannot affect the result. Mutexes are
        // re-entrant for their owning thread, so the second acquirer must be another thread.
        var name = $"PTZJoystickControlTests_{Guid.NewGuid():N}";
        using var first = SingleInstance.CreateMutex(name);
        Assert.True(first.WaitOne(0, false));
        try
        {
            bool secondAcquired = true;
            var other = new Thread(() =>
            {
                using var second = SingleInstance.CreateMutex(name);
                secondAcquired = second.WaitOne(0, false);
                if (secondAcquired)
                    second.ReleaseMutex();
            });
            other.Start();
            other.Join();

            Assert.False(secondAcquired);
        }
        finally
        {
            first.ReleaseMutex();
        }
    }
}
