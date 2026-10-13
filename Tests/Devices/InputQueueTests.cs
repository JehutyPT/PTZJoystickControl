using PtzJoystickControl.Application.Devices;
using Xunit;

namespace PtzJoystickControl.Tests.Devices;

public class InputQueueTests
{
    [Fact]
    public async Task Enqueue_FromAnotherThread_RunsOnlyWhenTheInputLoopRunsPending_OnTheInputLoopThread()
    {
        var queue = new InputQueue();
        int? ranOnThread = null;

        await Task.Run(() => queue.Enqueue(() => ranOnThread = Environment.CurrentManagedThreadId));
        Assert.Null(ranOnThread);

        queue.RunPending();

        Assert.Equal(Environment.CurrentManagedThreadId, ranOnThread);
    }

    [Fact]
    public void RunPending_RunsInputsInArrivalOrder_AndEachOnlyOnce()
    {
        var queue = new InputQueue();
        var ran = new List<string>();

        queue.Enqueue(() => ran.Add("press 1"));
        queue.Enqueue(() => ran.Add("release 1"));
        queue.Enqueue(() => ran.Add("press 2"));
        queue.RunPending();
        queue.RunPending();

        Assert.Equal(new[] { "press 1", "release 1", "press 2" }, ran);
    }

    [Fact]
    public void RunPending_WhenAnInputThrows_DoesNotThrow_AndStillRunsLaterInputs()
    {
        var queue = new InputQueue();
        var ran = new List<string>();

        queue.Enqueue(() => ran.Add("before"));
        queue.Enqueue(() => throw new InvalidOperationException("camera gone"));
        queue.Enqueue(() => ran.Add("after"));

        var thrown = Record.Exception(queue.RunPending);

        Assert.Null(thrown);
        Assert.Equal(new[] { "before", "after" }, ran);
    }
}
