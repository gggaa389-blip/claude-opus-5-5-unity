using UnityEngine;

/// One frame of player intent. Filled from the keyboard and mouse (HumanInput) or by the autopilot
/// (gameplay video), so the same controller code runs in both.
public struct PlayerInputState
{
    public Vector2 move;          // x strafe, y forward, each -1..1
    public Vector2 look;          // degrees this frame: x yaw, y pitch up
    public bool sprint, jump, fireHeld, firePressed, aim, aimPressed, reload, interact, map;
    public bool crouch;           // pressed this frame: crouch / stand up, or slide out of a sprint
    public bool edit;             // pressed this frame: edit the piece under the crosshair / confirm the edit
    public int slot;              // hotbar slot to select: 0 pickaxe, 1-5 inventory, -1 none
    public int build;             // build piece to switch to: 0 wall, 1 floor, 2 stairs, 3 cone, -1 none
    public int scroll;            // +1 next weapon, -1 previous
    public int emote;             // 1..6 play that emote, 9 open or close the emote wheel, 0 nothing
}

public interface IPlayerInputSource
{
    PlayerInputState Read();
}

/// Keyboard and mouse with the bindings from Settings (Fortnite's PC defaults: WASD, Shift sprint,
/// Space jump and mantle, LMB fire or use, RMB aim, R reload, E pick up / search / ride, 1-5 the slots,
/// F the pickaxe, wheel to cycle, M the map, Q Z C V build, G edit, Left Ctrl crouch / slide, B emotes).
/// Esc opens the pause menu (Hud); no gameplay input while a menu is open.
public class HumanInput : IPlayerInputSource
{
    public float aimScale = 1f;         // set by the controller when aiming down sights

    public PlayerInputState Read()
    {
        var s = new PlayerInputState { slot = -1, build = -1 };
        if (Hud.MenuOpen || CameraTools.Free || ReplayPlayer.Active) return s;
        if (Cursor.lockState != CursorLockMode.Locked)
        {
            if (Input.GetMouseButtonDown(0)) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
            return s;   // no gameplay input until the window is clicked
        }
        bool K(string a) => Input.GetKey(Settings.Key(a));
        bool D(string a) => Input.GetKeyDown(Settings.Key(a));
        float x = (K("Right") ? 1f : 0f) - (K("Left") ? 1f : 0f);
        float y = (K("Forward") ? 1f : 0f) - (K("Back") ? 1f : 0f);
        s.move = Vector2.ClampMagnitude(new Vector2(x, y), 1f);
        s.look = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y") * (Settings.InvertY ? -1f : 1f)) * Settings.Sensitivity * aimScale;
        s.sprint = K("Sprint");
        s.jump = D("Jump");
        s.fireHeld = Input.GetMouseButton(0);
        s.firePressed = Input.GetMouseButtonDown(0);
        s.aim = Input.GetMouseButton(1);
        s.aimPressed = Input.GetMouseButtonDown(1);
        if (D("Wall")) s.build = 0;
        if (D("Floor")) s.build = 1;
        if (D("Stairs")) s.build = 2;
        if (D("Cone")) s.build = 3;
        s.reload = D("Reload");
        s.crouch = D("Crouch");
        s.edit = D("Edit");
        s.interact = D("Interact");
        s.map = D("Map");
        if (D("Emote")) s.emote = 9;
        for (int i = 0; i < 5; i++)
            if (Input.GetKeyDown(KeyCode.Alpha1 + i)) s.slot = i + 1;
        if (D("Pickaxe")) s.slot = 0;
        float w = Input.GetAxisRaw("Mouse ScrollWheel");
        s.scroll = w > 0.01f ? -1 : w < -0.01f ? 1 : 0;
        return s;
    }
}
