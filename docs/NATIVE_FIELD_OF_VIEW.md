# Field of view

Each game's page has a **Field of view** setting: **Game default** or a
vertical angle from 65 to 90 degrees. The launcher writes it to
`field-of-view.txt` in the account's native folder (`default` or the angle),
and both adapters report `fieldOfViewRequested`, `fieldOfViewVerified` and
`fieldOfViewStatus` in `compat-status.json`. A requested angle that is not
verified stops the launch. Game default installs nothing.
Implementation: `tools/companion/native_field_of_view.h`.

## Original evidence

Both games use the engine's perspective matrix routine (thiscall matrix,
half angle in radians, width, height, near plane; `ret 0x10`). Row 1 is
`1/tan(half)` and row 0 is `height/(tan(half)*width)`, so the angle is
vertical and the horizontal angle follows the window's aspect ratio.

### Rainbow Six Vegas

- Perspective routine: `0x10b77190`.
- The scene view setup (`0x10b93060`, called by the viewport draw at
  `0x111d78eb` with no override) asks the player controller for its field of
  view (vtable `+0x5c0`, argument 0, `0x10b930eb`), clamps it to 1-179 in the
  view's `+0x84`, multiplies it by pi/360 (`0x10b93260`) and passes it to the
  routine. The calls return to `0x10b932f6` (camera with a fixed aspect
  ratio, camera `+0x258` bit 1 and `+0x260`) and `0x10b9339e`.
- The routine's third call in that setup (`0x10b93418`) uses the caller's
  separate foreground angle and is unchanged.
- The camera's view-projection (`0x1118b130`) multiplies the camera's field
  of view (vtable `+0x580`) by pi/360 and returns to `0x1118b29c`.
- `R6PlayerController::GetFOVAngle` (`0x111c92e0`) multiplies a scope's
  modifier by the weapons settings' `+0x254`, and uses `+0x254` (or 59.84,
  `0x12067300`, without settings) when the view has no angle. The settings
  object is also read from `0x123c1a74` at `0x111b0df4`.

### Rainbow Six Vegas 2

- Perspective routine: `0x10c14350`.
- The scene view setup (`0x111ba3d0`, called by the viewport draw at
  `0x10c7e86d` with no override) asks the player controller for its field of
  view (vtable `+0x67c`, argument 0, `0x111ba45e`), clamps it to 1-179 in the
  view's `+0x84`, multiplies it by pi and divides by 360 (`0x111ba64d`), and
  passes it to the routine, returning to `0x111ba6ef`. The second call
  (`0x111ba72d`) uses the foreground angle and is unchanged.
- The camera's view-projection multiplies the camera's field of view (vtable
  `+0x634`) by pi over 360 and returns to `0x10c15035` (fixed aspect ratio)
  or `0x10c15095`.
- `R6PlayerController::GetFOVAngle` (`0x10c6c9b0`) multiplies a scope's
  modifier by `+0x94` of the settings object at `0x124a8a44`, and uses `+0x94`
  when the view has no angle.

### Default angle

`59.84` is the Vegas 1 fallback above and the `FovAngle` in both games'
`DefaultEngine.ini`; it is 75 degrees horizontal at 4:3. The adapters read the
actual default from the settings object each time and use 59.84 only when the
object or its value is not available.

## Behavior

- The perspective routine is hooked. Only calls returning to the addresses
  above are changed; scene captures, shadows and other projections are not.
- Each half angle is scaled in tangent space by
  `tan(requested/2) / tan(default/2)`. The default view gets the requested
  angle, and zoomed and scoped views keep the same zoom relative to it. The
  result is limited to 170 degrees.
- The view's stored angle (`+0x84`), Vegas 2's zoom sensitivity ratio and
  every other caller of the controller and camera field of view are unchanged.
- All listed code is compared before installation. Any difference reports
  `codeMismatch` and installs nothing.

## Verification

- `tests/native/field_of_view_test.cpp` covers the request file, the scaling
  and the code checks with a synthetic image.
- The byte signatures were compared against decrypted copies of both games'
  executables. The setting has not been tested in a retail game launch.
