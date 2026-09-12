using ChatterFix.Core;
using ChatterFix.Core.Filtering;

namespace ChatterFix.Tests;

public class ClickFilterTests
{
    private const int ThresholdMs = 25;

    private static ClickFilter CreateFilter(
        FilterMode mode = FilterMode.Protect,
        int thresholdMs = ThresholdMs,
        bool enabled = true,
        bool leftEnabled = true)
    {
        var buttons = FilterSettings.CreateDefaultButtons();
        for (int i = 0; i < buttons.Length; i++)
            buttons[i] = buttons[i] with { ChatterThresholdMs = thresholdMs };

        buttons[(int)MouseButton.Left] = buttons[(int)MouseButton.Left] with { Enabled = leftEnabled };

        return new ClickFilter(new FilterSettings
        {
            Mode = mode,
            Enabled = enabled,
            Buttons = buttons,
        });
    }

    private static MouseEvent Down(double atMs, MouseButton button = MouseButton.Left, bool injected = false)
        => new(button, MouseEventKind.Down, (long)(atMs * 1000), injected);

    private static MouseEvent Up(double atMs, MouseButton button = MouseButton.Left, bool injected = false)
        => new(button, MouseEventKind.Up, (long)(atMs * 1000), injected);

    [Fact]
    public void HealthyClicks_AreNeverBlocked()
    {
        var filter = CreateFilter();

        // Three clicks at human speed, 150 ms apart.
        Assert.Equal(FilterAction.Pass, filter.Handle(Down(0)).Action);
        Assert.Equal(FilterAction.Pass, filter.Handle(Up(40)).Action);
        Assert.Equal(FilterAction.Pass, filter.Handle(Down(190)).Action);
        Assert.Equal(FilterAction.Pass, filter.Handle(Up(230)).Action);
        Assert.Equal(FilterAction.Pass, filter.Handle(Down(380)).Action);
        Assert.Equal(FilterAction.Pass, filter.Handle(Up(420)).Action);

        Assert.Equal(0, filter.Statistics[MouseButton.Left].ChatterSuppressed);
    }

    [Fact]
    public void JitterSpeedClicks_AreNeverBlocked()
    {
        var filter = CreateFilter();

        // 13 CPS jitter clicking: one click every 77 ms, held for 30 ms.
        double t = 0;
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(FilterAction.Pass, filter.Handle(Down(t)).Action);
            Assert.Equal(FilterAction.Pass, filter.Handle(Up(t + 30)).Action);
            t += 77;
        }

        Assert.Equal(0, filter.Statistics[MouseButton.Left].ChatterSuppressed);
        Assert.Equal(50, filter.Statistics[MouseButton.Left].Downs);
    }

    [Fact]
    public void ChatterPress_IsSuppressed()
    {
        var filter = CreateFilter();

        filter.Handle(Down(0));
        filter.Handle(Up(3));

        // A press 2 ms after the release: impossible for a human hand.
        var result = filter.Handle(Down(5));

        Assert.Equal(FilterAction.Suppress, result.Action);
        Assert.Equal(FilterReason.ChatterDown, result.Reason);
        Assert.Equal(1, filter.Statistics[MouseButton.Left].ChatterSuppressed);
    }

    [Fact]
    public void ReleaseOfASuppressedPress_IsSuppressedToo()
    {
        var filter = CreateFilter();

        filter.Handle(Down(0));
        filter.Handle(Up(3));
        filter.Handle(Down(5));                 // chatter, swallowed
        var result = filter.Handle(Up(20));     // its release

        // Letting this through would leave applications with a release and no press,
        // which is exactly how a button ends up stuck.
        Assert.Equal(FilterAction.Suppress, result.Action);
        Assert.Equal(FilterReason.OrphanUp, result.Reason);
        Assert.Equal(1, filter.Statistics[MouseButton.Left].OrphanSuppressed);
    }

    [Fact]
    public void PressExactlyAtTheThreshold_Passes()
    {
        var filter = CreateFilter(thresholdMs: 25);

        filter.Handle(Down(0));
        filter.Handle(Up(10));

        // Exactly 25 ms later. The rule is "shorter than the threshold", so this passes.
        Assert.Equal(FilterAction.Pass, filter.Handle(Down(35)).Action);
    }

    [Fact]
    public void PressJustUnderTheThreshold_IsSuppressed()
    {
        var filter = CreateFilter(thresholdMs: 25);

        filter.Handle(Down(0));
        filter.Handle(Up(10));

        Assert.Equal(FilterAction.Suppress, filter.Handle(Down(34.9)).Action);
    }

    [Fact]
    public void ChatterAfterALongHold_IsStillCaught()
    {
        var filter = CreateFilter();

        // Holding the button for two seconds, like mining a block.
        filter.Handle(Down(0));
        filter.Handle(Up(2000));

        // The fault lands right after the release. A filter comparing press to press
        // would see 2003 ms and miss it; measuring from the release catches it.
        var result = filter.Handle(Down(2003));

        Assert.Equal(FilterAction.Suppress, result.Action);
        Assert.Equal(FilterReason.ChatterDown, result.Reason);
    }

    [Fact]
    public void MonitorMode_BlocksNothing_ButStillMeasures()
    {
        var filter = CreateFilter(mode: FilterMode.Monitor);

        filter.Handle(Down(0));
        filter.Handle(Up(3));
        var result = filter.Handle(Down(5));

        Assert.Equal(FilterAction.Pass, result.Action);
        Assert.Equal(0, filter.Statistics[MouseButton.Left].ChatterSuppressed);

        // Measurement must continue regardless: calibration depends on it.
        Assert.Equal(1, filter.Statistics[MouseButton.Left].ReleaseGap.CountBelow(25));
    }

    [Fact]
    public void MasterSwitchOff_DisablesFiltering()
    {
        var filter = CreateFilter(enabled: false);

        filter.Handle(Down(0));
        filter.Handle(Up(3));

        Assert.Equal(FilterAction.Pass, filter.Handle(Down(5)).Action);
    }

    [Fact]
    public void DisabledButton_IsNotFiltered()
    {
        var filter = CreateFilter(leftEnabled: false);

        filter.Handle(Down(0));
        filter.Handle(Up(3));

        Assert.Equal(FilterAction.Pass, filter.Handle(Down(5)).Action);
    }

    [Fact]
    public void InjectedEvents_AreNeverFiltered()
    {
        var filter = CreateFilter();

        var result = filter.Handle(Down(0, injected: true));

        Assert.Equal(FilterAction.Pass, result.Action);
        Assert.Equal(FilterReason.Injected, result.Reason);
    }

    [Fact]
    public void Buttons_AreEvaluatedIndependently()
    {
        var filter = CreateFilter();

        filter.Handle(Down(0, MouseButton.Left));
        filter.Handle(Up(3, MouseButton.Left));

        // Chatter on the left button must not affect the right button's first press.
        Assert.Equal(FilterAction.Pass, filter.Handle(Down(5, MouseButton.Right)).Action);
        Assert.Equal(FilterAction.Suppress, filter.Handle(Down(6, MouseButton.Left)).Action);
    }

    [Fact]
    public void AChainOfChatter_IsFullySuppressed()
    {
        var filter = CreateFilter();

        filter.Handle(Down(0));
        filter.Handle(Up(3));

        // A switch whose spring keeps bouncing: three faulty press/release pairs.
        for (int i = 0; i < 3; i++)
        {
            double t = 5 + i * 4;
            Assert.Equal(FilterAction.Suppress, filter.Handle(Down(t)).Action);
            Assert.Equal(FilterAction.Suppress, filter.Handle(Up(t + 2)).Action);
        }

        Assert.Equal(3, filter.Statistics[MouseButton.Left].ChatterSuppressed);
        Assert.Equal(3, filter.Statistics[MouseButton.Left].OrphanSuppressed);

        // Once the chain ends, a real click must pass normally.
        Assert.Equal(FilterAction.Pass, filter.Handle(Down(300)).Action);
    }

    [Fact]
    public void TheFilter_NeverLeavesTheSystemUnbalanced()
    {
        // The guarantee that matters most: if swallowed events ever leave a button
        // pressed as far as the system is concerned, it sticks down in game. Never allowed.
        var filter = CreateFilter();
        var random = new Random(20260912);

        bool systemSeesButtonDown = false;
        double t = 0;

        for (int i = 0; i < 20_000; i++)
        {
            // Release gap: 20% of the time a fault interval (1-15 ms), otherwise human speed.
            t += random.Next(100) < 20 ? random.Next(1, 16) : random.Next(30, 400);

            var downResult = filter.Handle(Down(t));
            if (downResult.Action == FilterAction.Pass)
            {
                Assert.False(systemSeesButtonDown, $"step {i}: two presses in a row reached the system");
                systemSeesButtonDown = true;
            }

            t += random.Next(2, 250); // hold duration

            var upResult = filter.Handle(Up(t));
            if (upResult.Action == FilterAction.Pass)
            {
                Assert.True(systemSeesButtonDown, $"step {i}: a release reached the system with no press");
                systemSeesButtonDown = false;
            }
        }

        Assert.False(systemSeesButtonDown, "the button was left pressed at the end of the sequence");
    }

    [Fact]
    public void Settings_CanBeChangedWhileRunning()
    {
        var filter = CreateFilter(mode: FilterMode.Monitor);

        filter.Handle(Down(0));
        filter.Handle(Up(3));
        Assert.Equal(FilterAction.Pass, filter.Handle(Down(5)).Action);

        filter.UpdateSettings(filter.Settings with { Mode = FilterMode.Protect });

        filter.Handle(Up(8));
        Assert.Equal(FilterAction.Suppress, filter.Handle(Down(10)).Action);
    }
}
