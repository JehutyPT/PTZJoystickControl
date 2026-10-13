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

    [Fact]
    public void TryAcquire_WhenFree_AcquiresWithoutAbandonment()
    {
        using var mutex = SingleInstance.CreateMutex($"PTZJoystickControlTests_{Guid.NewGuid():N}");

        Assert.True(SingleInstance.TryAcquire(mutex, out bool abandoned));
        Assert.False(abandoned);
        mutex.ReleaseMutex();
    }

    [Fact]
    public void TryAcquire_WhileHeldElsewhere_DoesNotAcquire()
    {
        var name = $"PTZJoystickControlTests_{Guid.NewGuid():N}";
        using var first = SingleInstance.CreateMutex(name);
        Assert.True(first.WaitOne(0, false));
        try
        {
            bool secondAcquired = true;
            bool abandoned = true;
            var other = new Thread(() =>
            {
                using var second = SingleInstance.CreateMutex(name);
                secondAcquired = SingleInstance.TryAcquire(second, out abandoned);
                if (secondAcquired)
                    second.ReleaseMutex();
            });
            other.Start();
            other.Join();

            Assert.False(secondAcquired);
            Assert.False(abandoned);
        }
        finally
        {
            first.ReleaseMutex();
        }
    }

    [Fact]
    public void TryAcquire_AfterOwnerExitedWithoutReleasing_AcquiresAndReportsAbandonment()
    {
        // The previous instance dying while it owns the mutex: on Windows, and for .NET named
        // mutexes on Unix, an owner thread exiting without releasing abandons the mutex, and the
        // next WaitOne throws AbandonedMutexException.
        var name = $"PTZJoystickControlTests_{Guid.NewGuid():N}";
        // Another handle keeps the named mutex alive after the owner's handle closes, like a second
        // launch's "already running" window does.
        using var holder = SingleInstance.CreateMutex(name);
        bool ownerAcquired = false;
        var owner = new Thread(() =>
        {
            using var dying = SingleInstance.CreateMutex(name);
            ownerAcquired = dying.WaitOne(0, false);
            // Exits without ReleaseMutex.
        });
        owner.Start();
        owner.Join();
        Assert.True(ownerAcquired);

        using var next = SingleInstance.CreateMutex(name);
        Assert.True(SingleInstance.TryAcquire(next, out bool abandoned));
        Assert.True(abandoned);
        next.ReleaseMutex();
    }
}
