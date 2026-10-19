using DClean.Contracts;
using DClean.UI.Services;
using DClean.UI.ViewModels;
using Moq;

namespace DClean.Engine.Tests;

public class ProcessViewModelTests
{
    [Fact]
    public async Task RefreshAsync_LoadsProcessesFromClient()
    {
        var mock = new Mock<IPipeClient>();
        mock.Setup(x => x.EnsureConnectedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        mock.Setup(x => x.GetProcessesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new ProcessInfo(101, "alpha.exe", 10_000, 2_000, false, false),
                new ProcessInfo(202, "beta.exe", 20_000, 4_000, false, false),
            ]);

        var vm = new ProcessViewModel(mock.Object);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Processes.Count);
        Assert.Equal("alpha.exe", vm.Processes[0].Name);
        Assert.Contains("2", vm.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshAsync_WhenDisconnected_DoesNothing()
    {
        var mock = new Mock<IPipeClient>();
        mock.Setup(x => x.EnsureConnectedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var vm = new ProcessViewModel(mock.Object);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(vm.Processes);
        mock.Verify(x => x.GetProcessesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}