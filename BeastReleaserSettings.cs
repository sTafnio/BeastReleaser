using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

namespace BeastReleaser;

public class BeastReleaserSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [Menu("Start / Stop Hotkey", "Toggles the release loop. Requires the Bestiary > Captured Beasts tab to be open.")]
    public HotkeyNodeV2 ToggleHotkey { get; set; } = Keys.F6;


    [Menu("Max speed", "No fixed delays at all: each step only waits (frame by frame) until the game state changed - X visible, popup visible, beast gone.")]
    public ToggleNode MaxSpeed { get; set; } = new ToggleNode(false);

    [Menu("Action delay (ms)", "Pause between click and confirm steps (ignored in Max speed mode).")]
    public RangeNode<int> ActionDelayMs { get; set; } = new RangeNode<int>(100, 0, 1000);

    [Menu("Step timeout (ms)", "How long to wait for the X button / popup / list change before retrying.")]
    public RangeNode<int> StepTimeoutMs { get; set; } = new RangeNode<int>(2500, 500, 10000);


    [Menu("Show status overlay", "Draws the current state near the beast list.")]
    public ToggleNode ShowStatus { get; set; } = new ToggleNode(true);
}
