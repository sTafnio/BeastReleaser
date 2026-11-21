using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.Shared;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using RectangleF = SharpDX.RectangleF;

namespace BeastReleaser;

/// <summary>
/// Releases every captured beast from the Bestiary > Captured Beasts list.
/// The cursor is parked once on the red X (card child [3]) of the first card and never
/// moves again: click X -> wait for the confirm popup -> Enter -> wait for the list to shrink -> repeat,
/// because the next beast always slides into the same slot.
/// All waiting is frame-by-frame state polling (SyncTask + TaskUtils.NextFrame); the optional fixed
/// delays between steps are removed entirely in Max speed mode.
/// </summary>
public class BeastReleaser : BaseSettingsPlugin<BeastReleaserSettings>
{
    private SyncTask<bool> _task;
    private CancellationTokenSource _cts;
    private readonly Random _random = new();

    private string _status = "idle";
    private int _releasedThisRun;
    private int _startCount;

    public override bool Initialise() => true;

    public override void AreaChange(AreaInstance area)
    {
        if (_task != null) StopRun("area changed");
    }

    public override Job Tick()
    {
        if (Settings.ToggleHotkey.PressedOnce())
        {
            if (_task == null) StartRun();
            else StopRun("hotkey");
        }

        if (_task != null && GetCapturedBeastsTab() == null)
        {
            StopRun("captured beasts tab closed");
        }

        return null;
    }

    public override void Render()
    {
        if (_task != null)
        {
            TaskUtils.RunOrRestart(ref _task, () => null);
            if (_task == null)
            {
                // task finished on its own
                _cts?.Dispose();
                _cts = null;
            }
        }

        if (!Settings.ShowStatus) return;

        var tab = GetCapturedBeastsTab();
        if (tab == null) return;

        var rect = tab.GetClientRect();
        var pos = new Vector2(rect.X, rect.Y - 18);
        var count = GetBeastCards(tab).Count;
        var text = _task != null
            ? $"BeastReleaser: {_status}  ({_releasedThisRun} released, {count} left)"
            : $"BeastReleaser: idle  [{Settings.ToggleHotkey.Value}] releases {count} beasts";
        Graphics.DrawText(text, pos, _task != null ? Color.Orange : Color.LightGray);
    }

    private void StartRun()
    {
        var tab = GetCapturedBeastsTab();
        if (tab == null)
        {
            LogError("BeastReleaser: open the Bestiary > Captured Beasts tab first.", 5);
            return;
        }

        _releasedThisRun = 0;
        _startCount = GetBeastCards(tab).Count;
        _cts = new CancellationTokenSource();
        _status = "starting";
        LogMessage($"BeastReleaser: starting, {_startCount} beasts in list.", 3);
        _task = ReleaseLoop(_cts.Token);
    }

    private void StopRun(string reason)
    {
        if (_task == null) return;
        _cts?.Cancel();
        _task = null;
        _cts?.Dispose();
        _cts = null;
        _status = "idle";
        ReleaseInputs();
        LogMessage($"BeastReleaser: stopped ({reason}), released {_releasedThisRun}.", 3);
    }

    private static void ReleaseInputs()
    {
        try
        {
            if (Input.IsKeyDown(Keys.LButton)) Input.LeftUp();
            if (Input.IsKeyDown(Keys.Enter)) Input.KeyUp(Keys.Enter);
        }
        catch
        {
            // ignore
        }
    }

    // ExileCore's BestiaryTab.CapturedBeastsTab / CapturedBeastsTab.CapturedBeasts point at stale memory
    // since the bestiary was regrouped by family, so the tree is walked by child index instead:
    //   BestiaryTab[18]                captured beasts tab ([0] filter bar, [1] viewport, [2] scrollbar)
    //     [1]                          viewport (visible list area)
    //       [0]                        display root, one child per family
    //         [g][0]                   family header "- Reptiles (1)"
    //         [g][1][i]                beast card
    //           [1][0].Text            beast name
    //           [3]                    red X release button (only visible while hovered)
    private const int CapturedBeastsTabIndex = 18;
    private const int CardNameIndex = 1;
    private const int CardReleaseButtonIndex = 3;

    private Element GetCapturedBeastsTab()
    {
        try
        {
            var panel = GameController?.IngameState?.IngameUi?.ChallengesPanel;
            if (panel == null || !panel.IsVisible) return null;
            var bestiary = panel.TabContainer?.BestiaryTab;
            if (bestiary == null || !bestiary.IsVisible) return null;

            var fixedTab = bestiary.GetChildAtIndex(CapturedBeastsTabIndex);
            if (LooksLikeCapturedBeastsTab(fixedTab)) return fixedTab;

            // Index moved after a game patch: find it by shape.
            return bestiary.Children?.FirstOrDefault(LooksLikeCapturedBeastsTab);
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeCapturedBeastsTab(Element candidate)
    {
        try
        {
            if (candidate?.IsVisible != true || candidate.ChildCount < 3) return false;
            var groups = candidate.GetChildAtIndex(1)?.GetChildAtIndex(0);
            if (groups == null || groups.ChildCount <= 0 || groups.ChildCount > 64) return false;
            // every family group is a header + a card container
            return groups.Children.All(g => g?.ChildCount == 2);
        }
        catch
        {
            return false;
        }
    }

    private static Element GetViewport(Element tab) => tab?.GetChildAtIndex(1);

    /// <summary>All beast cards in display order (family by family, row by row), including ones scrolled out of view.</summary>
    private static List<Element> GetBeastCards(Element tab)
    {
        var result = new List<Element>();
        try
        {
            var groups = GetViewport(tab)?.GetChildAtIndex(0)?.Children;
            if (groups == null) return result;

            foreach (var group in groups)
            {
                if (group?.IsVisible != true) continue;
                var cards = group.GetChildAtIndex(1)?.Children;
                if (cards == null) continue;
                result.AddRange(cards.Where(c => c?.IsVisible == true));
            }
        }
        catch
        {
            // UI is changing under us; caller treats a short list as "try again next frame"
        }

        return result;
    }

    private static Element GetReleaseButton(Element card) => card?.GetChildAtIndex(CardReleaseButtonIndex);

    private static string GetBeastName(Element card)
    {
        try { return card?.GetChildAtIndex(CardNameIndex)?.GetChildAtIndex(0)?.Text; }
        catch { return null; }
    }

    private bool IsPopupVisible()
    {
        try
        {
            var popup = GameController?.IngameState?.IngameUi?.PopUpWindow;
            return popup != null && popup.IsVisible;
        }
        catch
        {
            return false;
        }
    }

    private int GetBeastCount()
    {
        var tab = GetCapturedBeastsTab();
        return tab == null ? -1 : GetBeastCards(tab).Count;
    }

    /// <summary>First beast whose card is fully inside the visible list area (the slot we park on).</summary>
    private Element GetFirstVisibleBeast()
    {
        var tab = GetCapturedBeastsTab();
        if (tab == null) return null;

        RectangleF listRect;
        try { listRect = GetViewport(tab)?.GetClientRect() ?? tab.GetClientRect(); }
        catch { return null; }
        foreach (var beast in GetBeastCards(tab))
        {
            RectangleF card;
            try { card = beast.GetClientRect(); }
            catch { continue; }

            if (card.Width <= 0 || card.Height <= 0) continue;
            // The list only scrolls vertically: skip cards partially scrolled out at the top/bottom edge.
            if (card.Top < listRect.Top || card.Bottom > listRect.Bottom) continue;
            return beast;
        }

        return null;
    }

    private static bool IsReleaseButtonVisible(Element beast)
    {
        try { return GetReleaseButton(beast)?.IsVisible == true; }
        catch { return false; }
    }

    private Vector2 ToScreen(float x, float y)
    {
        var win = GameController.Window.GetWindowRectangle();
        return new Vector2(x + win.X, y + win.Y);
    }

    private Vector2 RandomPointIn(RectangleF rect, float margin)
    {
        var w = Math.Max(1f, rect.Width - 2 * margin);
        var h = Math.Max(1f, rect.Height - 2 * margin);
        var x = rect.X + margin + (float)_random.NextDouble() * w;
        var y = rect.Y + margin + (float)_random.NextDouble() * h;
        return ToScreen(x, y);
    }

    /// <summary>Fixed pause between steps; a single frame in Max speed mode.</summary>
    private async SyncTask<bool> StepDelay(CancellationToken ct)
    {
        var baseMs = Settings.MaxSpeed ? 0 : Settings.ActionDelayMs.Value;
        if (baseMs <= 0)
        {
            ct.ThrowIfCancellationRequested();
            await TaskUtils.NextFrame();
            return true;
        }

        await Task.Delay(baseMs + _random.Next(0, Math.Max(1, baseMs / 3)), ct);
        return true;
    }

    /// <summary>Frame-by-frame poll until the condition holds or the step timeout expires.</summary>
    private async SyncTask<bool> WaitFor(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Settings.StepTimeoutMs.Value);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            bool ok;
            try { ok = condition(); }
            catch { ok = false; }
            if (ok) return true;
            await TaskUtils.NextFrame();
        }

        return false;
    }

    private async SyncTask<bool> ClickAtCursor(CancellationToken ct)
    {
        Input.LeftDown();
        await TaskUtils.NextFrame();
        Input.LeftUp();
        await StepDelay(ct);
        return true;
    }

    private async SyncTask<bool> PressEnter(CancellationToken ct)
    {
        Input.KeyDown(Keys.Enter);
        await TaskUtils.NextFrame();
        Input.KeyUp(Keys.Enter);
        await StepDelay(ct);
        return true;
    }

    private async SyncTask<bool> ReleaseLoop(CancellationToken ct)
    {
        var consecutiveFailures = 0;
        Vector2? parkedPos = null; // screen position of the X button; set once, never moved again

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (GetCapturedBeastsTab() == null)
                {
                    _status = "tab closed";
                    break;
                }

                var countBefore = GetBeastCount();
                if (countBefore <= 0)
                {
                    _status = "list empty";
                    LogMessage("BeastReleaser: no beasts left.", 3);
                    break;
                }


                if (consecutiveFailures >= 3)
                {
                    _status = "failed 3x, stopped";
                    LogError("BeastReleaser: 3 consecutive failures, stopping.", 5);
                    break;
                }

                var target = GetFirstVisibleBeast();
                if (target == null)
                {
                    _status = "no beast visible in list";
                    LogMessage("BeastReleaser: no beast card visible in the list (scroll to the top?).", 5);
                    break;
                }

                var beastName = GetBeastName(target) ?? "?";

                // 1. Make sure the X is showing. On the first beast we hover the card to reveal it and
                //    park the cursor on the X; afterwards the cursor already sits on the X of whatever
                //    beast slid into the slot.
                if (parkedPos == null)
                {
                    _status = $"hovering '{beastName}'";
                    Input.SetCursorPos(RandomPointIn(target.GetClientRect(), 20));
                    await StepDelay(ct);
                }

                _status = $"waiting for X on '{beastName}'";
                var xVisible = await WaitFor(() => IsReleaseButtonVisible(target), ct);
                if (!xVisible)
                {
                    // Nudge the cursor (the game sometimes needs a real move event) and look again.
                    var back = parkedPos ?? RandomPointIn(target.GetClientRect(), 20);
                    Input.SetCursorPos(back + new Vector2(3, 2));
                    await TaskUtils.NextFrame();
                    Input.SetCursorPos(back);
                    xVisible = await WaitFor(() => IsReleaseButtonVisible(target), ct);
                }

                if (!xVisible)
                {
                    consecutiveFailures++;
                    _status = "X button did not appear";
                    LogMessage($"BeastReleaser: release button never showed for '{beastName}' (attempt {consecutiveFailures}).", 3);
                    parkedPos = null; // re-hover next round
                    await StepDelay(ct);
                    continue;
                }

                if (parkedPos == null)
                {
                    parkedPos = RandomPointIn(GetReleaseButton(target).GetClientRect(), 2);
                    Input.SetCursorPos(parkedPos.Value);
                    await StepDelay(ct);
                    // the X must still be visible with the cursor on it
                    if (!await WaitFor(() => IsReleaseButtonVisible(target), ct))
                    {
                        consecutiveFailures++;
                        parkedPos = null;
                        continue;
                    }
                }

                // 2. Click the X (cursor is parked on it).
                _status = $"clicking X on '{beastName}'";
                await ClickAtCursor(ct);

                // 3. Confirm: wait for the popup, then a single Enter.
                _status = "waiting for popup";
                if (!await WaitFor(IsPopupVisible, ct))
                {
                    consecutiveFailures++;
                    _status = "popup not detected";
                    LogMessage($"BeastReleaser: confirmation popup not detected after clicking X (attempt {consecutiveFailures}).", 3);
                    await StepDelay(ct);
                    continue;
                }

                _status = $"confirming '{beastName}'";
                await PressEnter(ct);

                // 4. Wait until the list shrinks and the popup is gone.
                bool Removed()
                {
                    var c = GetBeastCount();
                    return c >= 0 && c < countBefore && !IsPopupVisible();
                }

                var removed = await WaitFor(Removed, ct);
                if (!removed && IsPopupVisible())
                {
                    // Enter did not take; try once more before counting a failure.
                    await PressEnter(ct);
                    removed = await WaitFor(Removed, ct);
                }

                if (!removed)
                {
                    consecutiveFailures++;
                    _status = "beast not removed";
                    LogMessage($"BeastReleaser: list did not shrink after confirming '{beastName}' (attempt {consecutiveFailures}).", 3);
                    await StepDelay(ct);
                    continue;
                }

                consecutiveFailures = 0;
                _releasedThisRun++;
                _status = $"released '{beastName}'";
                await StepDelay(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped by user / area change
        }
        catch (Exception ex)
        {
            _status = "error";
            LogError($"BeastReleaser: {ex.Message}", 5);
        }
        finally
        {
            ReleaseInputs();
        }

        LogMessage($"BeastReleaser: done, released {_releasedThisRun} of {_startCount}.", 5);
        return true;
    }
}
