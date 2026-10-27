# Crap Mechanics — Physics Objects / Cars and more!

> Pistons, bearings, electric and fuel motors, drive shafts, slip rings, wheels, pedals, a steering wheel, a turret seat, a recoupler, a beacon and small fuel tanks: build working cars, rovers, cranes and turrets in Approximately Up.

**Updating from 0.25.0 or older?** The mod's files now have English names: **delete the old BepInEx/plugins/Mecanica folder** and install the new BepInEx/plugins/CrapMechanics folder. While the old folder is still there the new version stays off (the BepInEx log says so). Nothing you made is lost: your settings and knob values are copied from the old files on the first launch, and the blueprints in the game's blueprint folder are converted to the new part names when the game starts (a .bak-0.27 copy of each changed file is kept). For a blueprint kept somewhere else, close the game and run **bp_convert.py** from the plugin folder (instructions inside it). In multiplayer, everyone needs the same version.

**Updating from 0.19.13?** The old **Piston 2**, **Piston 4** and **Piston 8** are no longer in the mod: they were replaced by a single Piston (up to 8 m) and the Piston End block, so blueprints that use the old pistons need them replaced with the new Piston. Motors now glue only by the bottom of their base and the small fuel tanks only by their brackets (like the game's tanks): in old blueprints, anything glued to their other faces needs to be glued again.

## Description

Crap Mechanics adds moving parts to Approximately Up. Pistons push, bearings spin, motors drive them, and wheels, pedals and a steering wheel turn a pile of frames into a car you can actually drive. Data and power reach the moving side through a **Slip Ring**, so nothing has to be left unpowered. Everything is made to look like the game's own parts, uses the game's ports and data cables (plus its own **Drive Shaft**), and lives in its own **Mechanics** tab of the inventory.

The name is a joke on a certain other building game. The physics are not a joke: every joint is a real Unity Physics joint, with soft starts, brakes and all the care needed so your contraptions don't explode (most of the time).

This mod was vibe-coded, I designed it and tested but didn't write the code.

## Installation instructions

1. Install **BepInEx 6.0.0-be.788 (IL2CPP, Windows x64)** into the game folder (the folder with ApproximatelyUp.exe) and run the game once, so BepInEx creates its folders.
2. Install **BepInEx Interop Patcher (by CohoJET, on Nexus Mods)**.
3. Install **Components Framework (by CohoJET, on Nexus Mods)**.
4. Extract this archive into **BepInEx/plugins** (you get **BepInEx/plugins/CrapMechanics/** with CrapMechanics.dll, Localization.json, the CrapMechanics asset bundle and bp_convert.py).
5. Start the game. The new parts are in the **Mechanics** tab of the garage inventory.

## Main features

- **Piston (up to 8 m)** and **Mini Piston** (0.25 m block, up to 4 m): push the block on top, proportional to the signal; status LED shows what they are doing. Put a Piston End or Mini Piston End plate on the piston's axis and the stroke becomes exactly that distance: the rod is already drawn extended in the garage, so you can build things that compress when the simulation starts. The knob shows the stroke in % and in meters; with the game's H display on, a translucent guide bar shows each piston's stroke.
- **Bearing**: free hinge, motor (a Drive Shaft from a Motor) or servo (a data signal holds an angle; the knob sets the range, 10 to 360 degrees). In motor mode a signal of 0 lets it coast; the motor's brake holds it. Small arrows show the direction it turns with a positive signal. A data output gives the angle it is at right now, in degrees or on the same scale as the input, so you can feed it back into your own logic.
- **Gyro Bearing**: keeps whatever is on top of it level against gravity (two stacked at 90 degrees = a gimbal for a camera, antenna or platform); a data signal adds an offset. **Mini Bearing** and **Mini Gyro Bearing** (0.25 m blocks, a quarter of the force): the same, for small mechanisms.
- **Slip Ring** and **Mini Slip Ring**: carry data and power across a moving joint, with no cable crossing it. Bearings, pistons, the Recoupler and the Turret Seat have pass-through ports; put a Slip Ring on the moving side and port 1 pairs with port 1, and so on. Each pair carries data either way, or power. The plugs take the colour of the cable plugged in, like the Frame Quarter With Ports.
- **Electric motors** (small / medium / large) and **fuel motors** fed by the game's pipes and any fuel mix; a motor's force is shared among the bearings it drives (one motor per wheel = a much stronger car); every motor has a brake input that locks the wheels it drives.
- **Drive Shaft**: the mod's own iron-colored cable that carries a motor's force to the bearings; a **Differential** splits one shaft into 4, a **Shaft Reverser** is a reverse gear and a **Shaft Transmitter** sends it wirelessly (steering knuckles, suspensions).
- **Wheels**: Sports and Offroad, 1 / 1.5 / 2 m; they only attach by the inner (flange) side.
- **Pedals** and **steering wheel** for driving; **turret seat** that aims with the mouse and turns everything glued to its plates, with pass-through ports on its base for each plate. Three knobs on the front of its base limit how far it turns: left and right, up, and down.
- **Recoupler**: re-attaches a part that a decoupler released, even in flight. **Beacon**: shows on every radar with its own icon (signal waves, car or ship, picked with its knob), and keeps a loose part simulated beyond 100 km from you (the game normally switches far parts off): a probe with a powered Beacon keeps its physics, camera, transmitter and thrusters working out there.
- **Single Cell Battery**, **Mini Solar Panel**, **Mini Electric Thruster** and a **Long Range Transmitter** (no distance limit, channel set by a data input; carries camera video too).
- **Small blue / green fuel tanks** (1x2x1, 500 fuel).
- Wheels, motors, small tanks and the turret seat take only the space of their shape (like the game's curved frames), so other parts and cables fit around them.
- **Mirror with V**: every asymmetric part has a mirrored version; a mirrored bearing turns the other way with the same signal, so the left and right wheels of a mirrored car both push forward.
- Works with blueprints, time rewind and the game's seat key bindings. Settings in the game's options menu with Mod Menu, including extra inventory amounts for any block (other mods' blocks too).

## Requirements

- BepInEx 6.0.0-be.788 (IL2CPP, Windows x64)
- BepInEx Interop Patcher (by CohoJET, on Nexus Mods)
- Components Framework (by CohoJET, on Nexus Mods)
- Mod Menu (optional: rebind this mod's keys and change its settings in the game's options menu)

Tested on the Steam version of Approximately Up from 08 October 2026 (update 1.0.214).

## Modding practice

No game files are redistributed. The archive contains only the mod's own code (and, for Crap Mechanics, its own meshes, icons and the asset bundle built with the official Modkit). Meshes, materials and fonts from the game are linked at run time by name; nothing is copied out of the game.

Install and uninstall by adding or removing the mod's folder under BepInEx/plugins. Settings live in BepInEx/config and in the folder itself; blueprints and saves are never modified by the mod.

Please do not re-upload the archive elsewhere; link to the Nexus page instead.

A few parts (the small fuel tanks, the turret seat, the Shaft Transmitter, the Long Range Transmitter, the Mini Electric Thruster, the Mini Solar Panel and the Drive Shaft cable) are set up as Modkit copies of the game's own parts with changed sizes and numbers; their prefab definitions are therefore derived from the game; only the generated bundle ships. Everything else is modelled from scratch.

## Shout outs

- **CohoJET** for the BepInEx Interop Patcher and the Components Framework, which made modding this game possible.
- The **BepInEx** and **Il2CppInterop** teams.
- **MicroFrames** by CohoJET, which I've been loving to build with: the tiny frames are perfect for fitting mechanisms in tight spots.
- **Approximately Games** for Approximately Up.

## Other mods I'm working on

- **Replay mod**: Record your adventures and replay it after.
- **Mod menu**: Adds a menu tab for configuring your mods.
- **Extended Controller Support**: Adds support for gaming controllers with a bunch of functionalities.
- **Camera 3P**: Adds a different POV for the player (like minecraft F5).
- **Quick Start**: Skips the main menu and the planet selection, going straight to the last saved location.
- **Time Rewind Binds**: Adds configurable keybinds to Pause, Accelerate time and the Time Rewind panel buttons.

<details><summary>Nexus description (HTML, ready to paste)</summary>

```html
<h2>Crap Mechanics <em>- Physics Objects / Cars and more!</em></h2>
<p><strong>Updating from 0.25.0 or older?</strong> The mod&#x27;s files now have English names: <strong>delete the old BepInEx/plugins/Mecanica folder</strong> and install the new BepInEx/plugins/CrapMechanics folder. While the old folder is still there the new version stays off (the BepInEx log says so). Nothing you made is lost: your settings and knob values are copied from the old files on the first launch, and the blueprints in the game&#x27;s blueprint folder are converted to the new part names when the game starts (a .bak-0.27 copy of each changed file is kept). For a blueprint kept somewhere else, close the game and run <strong>bp_convert.py</strong> from the plugin folder (instructions inside it). In multiplayer, everyone needs the same version.</p>
<p><strong>Updating from 0.19.13?</strong> The old <strong>Piston 2</strong>, <strong>Piston 4</strong> and <strong>Piston 8</strong> are no longer in the mod: they were replaced by a single Piston (up to 8 m) and the Piston End block, so blueprints that use the old pistons need them replaced with the new Piston. Motors now glue only by the bottom of their base and the small fuel tanks only by their brackets (like the game&#x27;s tanks): in old blueprints, anything glued to their other faces needs to be glued again.</p>
<h3>Description</h3>
<p>Crap Mechanics adds moving parts to Approximately Up. Pistons push, bearings spin, motors drive them, and wheels, pedals and a steering wheel turn a pile of frames into a car you can actually drive. Data and power reach the moving side through a <strong>Slip Ring</strong>, so nothing has to be left unpowered. Everything is made to look like the game&#x27;s own parts, uses the game&#x27;s ports and data cables (plus its own <strong>Drive Shaft</strong>), and lives in its own <strong>Mechanics</strong> tab of the inventory.</p>
<p>The name is a joke on a certain other building game. The physics are not a joke: every joint is a real Unity Physics joint, with soft starts, brakes and all the care needed so your contraptions don&#x27;t explode (most of the time).</p>
<p>This mod was vibe-coded, I designed it and tested but didn&#x27;t write the code.</p>
<h3>Installation instructions</h3>
<ol>
<li>Install <strong>BepInEx 6.0.0-be.788 (IL2CPP, Windows x64)</strong> into the game folder (the folder with ApproximatelyUp.exe) and run the game once, so BepInEx creates its folders.</li>
<li>Install <strong>BepInEx Interop Patcher (by CohoJET, on Nexus Mods)</strong>.</li>
<li>Install <strong>Components Framework (by CohoJET, on Nexus Mods)</strong>.</li>
<li>Extract this archive into <strong>BepInEx/plugins</strong> (you get <strong>BepInEx/plugins/CrapMechanics/</strong> with CrapMechanics.dll, Localization.json, the CrapMechanics asset bundle and bp_convert.py).</li>
<li>Start the game. The new parts are in the <strong>Mechanics</strong> tab of the garage inventory.</li>
</ol>
<h3>Main features</h3>
<ul>
<li><strong>Piston (up to 8 m)</strong> and <strong>Mini Piston</strong> (0.25 m block, up to 4 m): push the block on top, proportional to the signal; status LED shows what they are doing. Put a Piston End or Mini Piston End plate on the piston&#x27;s axis and the stroke becomes exactly that distance: the rod is already drawn extended in the garage, so you can build things that compress when the simulation starts. The knob shows the stroke in % and in meters; with the game&#x27;s H display on, a translucent guide bar shows each piston&#x27;s stroke.</li>
<li><strong>Bearing</strong>: free hinge, motor (a Drive Shaft from a Motor) or servo (a data signal holds an angle; the knob sets the range, 10 to 360 degrees). In motor mode a signal of 0 lets it coast; the motor&#x27;s brake holds it. Small arrows show the direction it turns with a positive signal. A data output gives the angle it is at right now, in degrees or on the same scale as the input, so you can feed it back into your own logic.</li>
<li><strong>Gyro Bearing</strong>: keeps whatever is on top of it level against gravity (two stacked at 90 degrees = a gimbal for a camera, antenna or platform); a data signal adds an offset. <strong>Mini Bearing</strong> and <strong>Mini Gyro Bearing</strong> (0.25 m blocks, a quarter of the force): the same, for small mechanisms.</li>
<li><strong>Slip Ring</strong> and <strong>Mini Slip Ring</strong>: carry data and power across a moving joint, with no cable crossing it. Bearings, pistons, the Recoupler and the Turret Seat have pass-through ports; put a Slip Ring on the moving side and port 1 pairs with port 1, and so on. Each pair carries data either way, or power. The plugs take the colour of the cable plugged in, like the Frame Quarter With Ports.</li>
<li><strong>Electric motors</strong> (small / medium / large) and <strong>fuel motors</strong> fed by the game&#x27;s pipes and any fuel mix; a motor&#x27;s force is shared among the bearings it drives (one motor per wheel = a much stronger car); every motor has a brake input that locks the wheels it drives.</li>
<li><strong>Drive Shaft</strong>: the mod&#x27;s own iron-colored cable that carries a motor&#x27;s force to the bearings; a <strong>Differential</strong> splits one shaft into 4, a <strong>Shaft Reverser</strong> is a reverse gear and a <strong>Shaft Transmitter</strong> sends it wirelessly (steering knuckles, suspensions).</li>
<li><strong>Wheels</strong>: Sports and Offroad, 1 / 1.5 / 2 m; they only attach by the inner (flange) side.</li>
<li><strong>Pedals</strong> and <strong>steering wheel</strong> for driving; <strong>turret seat</strong> that aims with the mouse and turns everything glued to its plates, with pass-through ports on its base for each plate. Three knobs on the front of its base limit how far it turns: left and right, up, and down.</li>
<li><strong>Recoupler</strong>: re-attaches a part that a decoupler released, even in flight. <strong>Beacon</strong>: shows on every radar with its own icon (signal waves, car or ship, picked with its knob), and keeps a loose part simulated beyond 100 km from you (the game normally switches far parts off): a probe with a powered Beacon keeps its physics, camera, transmitter and thrusters working out there.</li>
<li><strong>Single Cell Battery</strong>, <strong>Mini Solar Panel</strong>, <strong>Mini Electric Thruster</strong> and a <strong>Long Range Transmitter</strong> (no distance limit, channel set by a data input; carries camera video too).</li>
<li><strong>Small blue / green fuel tanks</strong> (1x2x1, 500 fuel).</li>
<li>Wheels, motors, small tanks and the turret seat take only the space of their shape (like the game&#x27;s curved frames), so other parts and cables fit around them.</li>
<li><strong>Mirror with V</strong>: every asymmetric part has a mirrored version; a mirrored bearing turns the other way with the same signal, so the left and right wheels of a mirrored car both push forward.</li>
<li>Works with blueprints, time rewind and the game&#x27;s seat key bindings. Settings in the game&#x27;s options menu with Mod Menu, including extra inventory amounts for any block (other mods&#x27; blocks too).</li>
</ul>
<h3>Requirements</h3>
<ul>
<li>BepInEx 6.0.0-be.788 (IL2CPP, Windows x64)</li>
<li>BepInEx Interop Patcher (by CohoJET, on Nexus Mods)</li>
<li>Components Framework (by CohoJET, on Nexus Mods)</li>
<li>Mod Menu (optional: rebind this mod&#x27;s keys and change its settings in the game&#x27;s options menu)</li>
</ul>
<p>Tested on the Steam version of Approximately Up from 08 October 2026 (update 1.0.214).</p>
<h3>Modding practice</h3>
<p>No game files are redistributed. The archive contains only the mod&#x27;s own code (and, for Crap Mechanics, its own meshes, icons and the asset bundle built with the official Modkit). Meshes, materials and fonts from the game are linked at run time by name; nothing is copied out of the game.</p>
<p>Install and uninstall by adding or removing the mod&#x27;s folder under BepInEx/plugins. Settings live in BepInEx/config and in the folder itself; blueprints and saves are never modified by the mod.</p>
<p>Please do not re-upload the archive elsewhere; link to the Nexus page instead.</p>
<p>A few parts (the small fuel tanks, the turret seat, the Shaft Transmitter, the Long Range Transmitter, the Mini Electric Thruster, the Mini Solar Panel and the Drive Shaft cable) are set up as Modkit copies of the game&#x27;s own parts with changed sizes and numbers; their prefab definitions are therefore derived from the game; only the generated bundle ships. Everything else is modelled from scratch.</p>
<h3>Shout outs</h3>
<ul>
<li><strong>CohoJET</strong> for the BepInEx Interop Patcher and the Components Framework, which made modding this game possible.</li>
<li>The <strong>BepInEx</strong> and <strong>Il2CppInterop</strong> teams.</li>
<li><strong>MicroFrames</strong> by CohoJET, which I&#x27;ve been loving to build with: the tiny frames are perfect for fitting mechanisms in tight spots.</li>
<li><strong>Approximately Games</strong> for Approximately Up.</li>
</ul>
<h3>Other mods I'm working on</h3>
<ul>
<li><strong>Replay mod</strong>: Record your adventures and replay it after.</li>
<li><strong>Mod menu</strong>: Adds a menu tab for configuring your mods.</li>
<li><strong>Extended Controller Support</strong>: Adds support for gaming controllers with a bunch of functionalities.</li>
<li><strong>Camera 3P</strong>: Adds a different POV for the player (like minecraft F5).</li>
<li><strong>Quick Start</strong>: Skips the main menu and the planet selection, going straight to the last saved location.</li>
<li><strong>Time Rewind Binds</strong>: Adds configurable keybinds to Pause, Accelerate time and the Time Rewind panel buttons.</li>
</ul>
```

</details>

# Details
A mod for **Approximately Up** (BepInEx 6 IL2CPP) that adds pistons, bearings, electric and fuel motors, wheels, pedals, a steering
wheel, a turret seat, a recoupler, a beacon and fuel tanks: everything you need to build working cars, rovers, cranes and turrets.
All blocks live in the **Mechanics** tab of the inventory and are always unlocked. Version **0.28.2**, by Andre + Claude.



## Blocks

| Block | What it does |
|---|---|
| **Piston** / **Mini Piston** | Pushes the block on its top face up to 8 m (Mini: 0.25 m block, up to 4 m, a quarter of the force), proportional to the signal (0..1); with a **Piston End** on its axis the stroke is the distance to it and the rod is born extended. The old Piston 2 / 4 / 8 still work in old blueprints (hidden from the inventory). Power: 2.5 P/s per meter of stroke while moving; without power it holds its position. Status LED: green = pushing, yellow = guided (closes a ring of joints), red = welded. |
| **Piston End** | 0.25 m block that marks where a Piston ends. Glues on all 6 faces; place it on the piston's axis (any distance up to the piston's maximum, on the garage grid), attached to what the piston will push. The piece attached to it is separate in the garage (the garage bot flags it; that is expected). |
| **Mini Bearing** / **Mini Gyro Bearing** | 0.25 m versions with a single untyped input in the middle of the bottom face and a quarter of the force. |
| **Bearing** | The top face spins around the axis. Its 4 ports have no type (like the Frame Quarter with Ports): a **Drive Shaft** from a Motor = **motor** (speed and direction from the motor's signal, 0 brakes); a **data** cable (knob, lever, steering wheel) = **servo** (−1..1 → −45°..45°, car steering). Both wired: the shaft drives while it turns, and the servo takes over when the shaft is at 0. Nothing wired = spins freely. |
| **Small / Medium / Large Motor** (electric) | Input −1..1, **shaft output**: a Drive Shaft to a Bearing, or to a Differential for several. Uses up to 7 / 28 / 70 P/s. Without power the bearings spin freely. The 4th (yellow) port is a mechanical brake that locks all wheels of the vehicle. |
| **Fuel Motor Small / Medium / Large** | Like the electric one, 50% stronger. Takes fuel through the pipe connection (same face as the ports, bottom corner), burns up to 0.85/s at signal 1 and accepts any mix (the force follows the mix efficiency). Uses 5 P/s of power while turning. Without fuel it brakes. |
| **Drive Shaft** | A cable (placed like the game's cables, iron colored) that only connects shaft ports: motor output, bearing ports, Differential and Shaft Reverser. It carries force, not a value. The game's own plasma cable does not transmit (the log warns). |
| **Differential** | 2×2×2. 4 shaft inputs (orange) on one face, 4 outputs (purple) on the other. Motors on the inputs add up (force summed, speed and signal averaged by force); the total is split among every bearing reached. A shaft that goes into an output does not transmit. Motors turning against each other lock their bearings (red LED on the bearing). |
| **Shaft Reverser** | 2×2×1, like the game's math blocks. Reverse gear: the bearings after it turn the other way (+1 becomes −1). |
| (shaft ports) | Orange = shaft input, purple = shaft output, half and half = both (Shaft Transmitter). |
| **Shaft Transmitter** | 2×2×2, the game's Wireless Transmitter for shafts (same lever and channel number, iron colored). A Drive Shaft that goes into one comes out of every other Shaft Transmitter on the same channel, on any vehicle — for wheels on a steering knuckle or a suspension, where a cable can't cross the joint. Its channels are separate from the data transmitters'. |
| **Single Cell Battery** | 2×4×2: one cell of the game's small rechargeable battery (500 P, a quarter of it), with the game's charge bar. |
| **Mini Electric Thruster** | The game's small electric thruster at half size (0.5 × 1 × 0.5): a quarter of the force and power use, same glow/flame/reverse button. |
| **Long Range Transmitter** | Wireless data with no distance limit. 2 ports: signal (wire it to an output to send, to an input to receive) and channel (data input, can change during the simulation; the screen shows it). Own channels. |
| **Small Blue / Green Tank** | 1×2×1 m, 500 fuel, comes full. Valve, gauge and fuel-level data output, like the game's tanks. |
| **Sports / Offroad Wheels (Small / Medium / Large)** | 1 / 1.5 / 2 m wheels to put on top of a Bearing. Sports = light, friction 1.5; Offroad = heavier, friction 2.2. |
| **Pedals** | Two floor pedals in one block with one output: left = −1, right = +1, none = 0. |
| **Steering Wheel** | Works like a Horizontal Joystick (−1..1, springs back to the middle); wire it to bearings in servo mode. |
| **Turret Seat** | Sit down and aim with the mouse: everything glued to its 4 plates turns with your view (up to a full circle sideways and 135° up or down; the 3 knobs on the front of the base set each limit, by default free / 135° up / 30° down, also when the whole ship turns), no bearings needed. The cursor key (Left Ctrl by default) shows/hides the cursor. |
| **Recoupler** | The opposite of the decoupler: with signal ≥ 0.5, if it is touching (within 0.5 m and 30°) the face of a Decoupler that has already fired, it pulls the piece into place and merges both parts again (the decoupler works again afterwards). |
| **Beacon** | Shows on every Radar with its own icon, in real time, while powered (5 P/s). Its knob picks the icon — signal waves, car or ship — and the small display shows it. Hides from the radar of its own vehicle. |
| (bonus) | Each unlocked motor gives extra cables; Recoupler/Beacon/Bearings/motor/wheels unlock +1 Radar. |

Most blocks have a mirrored version (press **V** in the garage), so the left and right wheels of a mirrored car both push forward.

## How the physics works (summary)

- In the simulation, the top face of a piston/bearing is released like a decoupler, and the mod connects the two parts with a Unity
  Physics joint (piston = position motor + 4 rails; bearing = hinge + 2 support points on the axis; motor/servo = velocity motor).
- **Staggered start**: everything spawns locked; after `soft_start` the joints are unlocked one at a time (`start_stagger`): first the
  bearings attached to the chassis, then the ones above them (wheel on the steering knuckle), on the same level the lightest first;
  pistons last.
- At start a wheel doesn't collide with the parts above it in the bearing chain (steering wheel vs. chassis); collision comes back
  `Wheel.no_collision_at_start` seconds later.
- The servo is implemented by the mod (the Unity Physics angle motor made a full turn when the rotation came with the opposite sign).
- When the game rebuilds the parts (decoupler, breakage), the joint is recreated in the same frame, in the same pose.
- Parts are linked as a tree: each part is attached to the base by ONE path of joints. A piston/bearing that would close a ring of
  joints stays welded or runs free/guided, and the log says why.
- **Turret link**: a structure attached to 2+ faces of the turret (plate+plate, plate+base, 3, 4…) becomes a *link*: it turns with
  the turret, but with normal inertia and limited acceleration (`Turret Seat.link_acceleration`, °/s²), so it doesn't explode when a
  wheel touches the ground. A plate on its own (one face) works as before. If the base and the plates are on the SAME structure and
  the base has nothing else, the whole ship turns with the aim.

## Mark error (debug)

Set `Debug.mark_error_key` (e.g. `F8`; off by default) or bind it in the options menu. When you see something wrong, press it: the log
gets a line `===== ERROR MARK #n — mark key pressed at HH:MM:SS.mmm =====` with the state of every joint, motor (fuel included),
recoupler and beacon, and a screenshot goes to `logs/marks/`. A small window opens so you can write what happened, with a "Send with
screenshot" checkbox (Ctrl+Enter sends, Esc cancels; an unchecked screenshot is moved to `logs/_old`).

## Logs

`BepInEx/plugins/CrapMechanics/logs/session-YYYYMMDD-HHMMSS.log`: one per session, with everything that goes through BepInEx. The 5 most
recent are kept; older ones are moved to `logs/_old` (nothing is deleted).

## Config (`BepInEx/config/com.approximatelyup.mods.crapmechanics.cfg`)

Since 0.13.0 the sections and keys are in English. Values from older (Portuguese) configs are carried over automatically on the
first launch. Up to 0.26.x the file was `com.approximatelyup.mods.mecanica.cfg` and the knob values were in `config/Mecanica/knobs.txt`:
0.27.0 copies both to the new names on its first launch (`config/CrapMechanics/knobs.txt`) and leaves the old files in place.

- `General`: `enabled`, `detailed_log`.
- `Piston`: `speed` (m/s), `max_impulse`, `power_per_meter_v2`, `rails`, `rails_per_meter`, `max_tilt`, `snap_back`, `collide_with_load`,
  `whole_until_moved`, `mass_ratio_max`.
- `Small Motor` / `Medium Motor` / `Large Motor`: `max_speed`, `max_impulse`, `power`.
- `Small Fuel Motor` / `Medium Fuel Motor` / `Large Fuel Motor`: `max_speed`, `max_impulse`. `Fuel Motor`: `fuel_per_second`, `power`.
- `Motor`: `ramp`, `brake_factor`, `force_pool` (4 = each motor's force, split among the bearings it drives).
- `Bearing`: `servo_impulse`, `servo_degrees`, `servo_speed`, `servo_gain`, `servo_tip_speed`, `servo_invert`, `extra_support`.
- `Wheel`: `friction`, `offroad_friction`, `no_collision_at_start`.
- `Physics`: `rigid_joints`, `zero_gravity_on_launch`, `soft_start`, `start_stagger`, `fit_wait`, `solver_iterations`,
  `solver_iterations_rigid`, `base_reinforcement`.
- `Turret Seat`: `sensitivity`, `gain`, `max_speed`, `link_acceleration`, `seat_mode` (`cockpit` / `A` / `B`),
  `seat_offset`, `player_position_b`, `view_position_a`, `view_a_rotates`, `infinite_inertia`, `camera`, `cursor_key`, `bindings_reach`,
  `whole_until_aimed`.
- `Recoupler`: `tolerance`, `max_angle`, `pull_time`, `merge`.
- `Beacon`: `power`, `hide_within`, `hide_on_own_vehicle` (false = also shows on your own vehicle's radar), `icon`, `icon_slot` (preferred slot),
  `keep_simulated_far`.
- `Seat`: `bindings_reach` (how far the key bindings of the game's normal seat reach).
- `Signal`: `fix_stuck_ports`.
- `Bonus`: `extra_blocks` (`SC_Name=amount, ...`: extra inventory amount of any block, other mods' too; the pre-0.27.0 names of this mod's
  blocks are accepted too).
- `Multiplayer`: `host_only` (the server runs the mod's physics; clients only draw), `break_on_client`, `ring_on_client`,
  `fake_client`, `net_log` (debug), `test_loopback` (test the turret seat's network path in a solo game). See *Multiplayer*.
- `Debug`: `mark_error_key`, `turret_adjust_numpad`, `trace`.

Changes made in the options menu ("Mod settings", with Mod Menu) apply immediately.

## Building

- Plugin: `Plugin/CrapMechanics.csproj` (Modkit format; in the Modkit workspace the mod lives in `Assets/Mods/CrapMechanics`).
  `dotnet build CrapMechanics.csproj -c Release -p:ModkitGameDir="...\Approximately Up" -p:CFDir=<folder with ComponentsFramework.dll>`.
- Prefabs: `python3 gen_prefabs.py <Content/Prefabs> <workspace>/Assets/Game/GameObject` (generated YAML; the tanks are copies of the
  game's `SC_GreenTank`). Then, in the Modkit's Unity editor: **Modkit > Mods > Build (+ Deploy)** to produce the AssetBundle.
- Nothing from the Modkit workspace (derived from the game) is published.

## Multiplayer (0.28.0, experimental)

Everyone in the game needs the same version of Crap Mechanics (the log compares them when a player joins). The game always hosts,
even alone (so friends can join): the rules below apply only while another player is in the game, or when you join someone else's. The game is not lockstep:
the server owns the parts and sends their poses to the clients. So:

- The **server** runs everything the mod does to physics: splitting the ship at launch, the joints, motors, pistons, bearings, brakes,
  the Turret Seat, the Recoupler and the knob values saved in `knobs.txt`.
- A **client** never touches physics or the game's joints. It draws the mod's parts from the poses the server sends (piston rods and
  heads, the bearing's angle output, the Turret Seat's seat and rings) and its knob changes reach the server through the game itself.
- The **Turret Seat** of a player sitting on a client: the client adds that player's mouse to the aim and sends it to the server over
  the mod's own channel (Steam networking messages, channel 7741, separate from the game's connection); the server turns the turret.
- The **Recoupler** only locks on a network (merging two parts is not something the game sends to the other players).
- Parts farther than 100 km follow the game's own rule on a network (the beacon exemption only works in a solo game).
- Replay Mod 0.10.0+ does not freeze the world during playback on a network, and the mod keeps running meanwhile.

Test settings: `[Multiplayer] test_loopback` (solo game: the Turret Seat aim goes through the network message path and comes back as if
another player had sent it), `fake_client` (solo game: this machine behaves like a client, nothing of the mod moves) and `net_log`
(every message of the mod's channel in the log). If on a client the top of a piston or bearing jumps or stays stuck to its base, try
`break_on_client = true` on that client and report what changed.

## Old blueprints and part names (0.27.0)

Up to 0.26.x some parts had Portuguese prefab names; 0.27.0 renamed them (the mirrored `... M` versions too):

| Before 0.27.0 | From 0.27.0 |
|---|---|
| `SC_Pistao` | `SC_Piston` |
| `SC_MiniPistao` | `SC_MiniPiston` |
| `SC_PistaoFim` | `SC_PistonEnd` |
| `SC_MiniPistaoFim` | `SC_MiniPistonEnd` |
| `SC_MotorP / M / G` | `SC_MotorSmall / Medium / Large` |
| `SC_MotorCombP / M / G` | `SC_MotorFuelSmall / Medium / Large` |
| `SC_RodaSportsP / M / G` | `SC_WheelSportsSmall / Medium / Large` |
| `SC_RodaOffroadP / M / G` | `SC_WheelOffroadSmall / Medium / Large` |
| `SC_Volante` | `SC_SteeringWheel` |
| `SC_TanqueAzul` | `SC_TankSmallBlue` |
| `SC_TanqueVerde` | `SC_TankSmallGreen` |

A blueprint stores each part by the hash of its prefab name. When the game starts, before it reads any blueprint, the mod goes through the
`.bp` files of the game's blueprint folder (subfolders too) and swaps the old hashes for the new ones; each changed file is copied to
`<name>.bp.bak-0.27` first (the log says `old prefab names: <file> — N part(s) renamed` and, at the end, how many were converted). A file
that doesn't follow the format to the last byte is left alone. A converted file has no old name left, so later starts only read it.

Offline converter, for blueprints kept somewhere else (a copy, a download) or to convert a folder by hand:

1. Start the game once with 0.27.0: it writes `BepInEx/config/CrapMechanics/prefab-renames.txt` (both names and both hashes of every
   renamed part, as the game computes them).
2. Close the game and, in the game folder, run
   `python BepInEx/plugins/CrapMechanics/bp_convert.py BepInEx/config/CrapMechanics/prefab-renames.txt --dry-run`
   to see what would change, then the same without `--dry-run` to convert. With no blueprint path it converts the game's blueprint folder
   (`%USERPROFILE%/AppData/LocalLow/ApproximatelyGames/ApproximatelyUp/Blueprints`); pass `.bp` files or folders to pick others.
   Each changed file is copied to `<name>.bp.bak-0.27` first, and a file it doesn't fully understand is left alone.

## Changelog
- 0.28.2: ships and cars came apart at launch: in 0.28.1 a comment swallowed the line that installs the mod's game hooks, so the joint
  was no longer created in the same physics tick in which the game splits the parts (and the wheel friction, the hidden mirrored parts,
  the world unload pause, the radar icon and the time rewind button were off too). Also: a solo game counted as a network game (the
  game always hosts, as ServerOpen), so the Recoupler only locked, beacons lost the 100 km exemption and Replay playback didn't freeze;
  now the network rules apply only with another player in the game.
- 0.28.1: the game closed by itself while starting, right after the Components Framework applied the texts: 0.27.0/0.28.0 converted old
  blueprints with a hook on a function the game calls for every saved blueprint at start, and that hook crashed. Old blueprints are now
  converted on disk at start, before the game reads them, keeping a `.bak-0.27` copy of each changed file. The transition log works again.
- 0.28.0: **multiplayer** (experimental, see *Multiplayer*) — the server runs the mod's physics and clients only draw the mod's parts
  from the server's poses; a player seated in a Turret Seat on a client aims it (the aim goes to the server over the mod's own Steam
  channel, which also compares the mod versions and plugin lists of the players); knob values are restored only by the server; the
  Recoupler locks without merging and the 100 km rule is the game's on a network. Before, a client broke the game's joints of every
  piston/bearing on its own copy of the ship at launch and could drive the server's parts. New `[Multiplayer] net_log` and
  `test_loopback`.
- 0.27.0: **English release** — everything is in English, code included. Plugin folder `BepInEx/plugins/CrapMechanics`
  (`CrapMechanics.dll`, `CrapMechanics` asset bundle), config `com.approximatelyup.mods.crapmechanics.cfg`, knob values
  `config/CrapMechanics/knobs.txt`. The old `.cfg` and `knobs.txt` are copied on the first launch; the new version stays off while
  `plugins/Mecanica` is still installed (both would register every block twice). Parts with Portuguese prefab names were renamed (see
  *Old blueprints and part names*): old blueprints in the game's blueprint folder are converted on disk when the game starts, with `bp_convert.py` for any other folder.
  The mirrored (V) wheels are generated again (they were left over from 0.19, before parts took only the space of their shape): they now do, like
  the normal wheels.
  Everything from the 0.26.x test builds is in it too:
- 0.26.x (test builds): **rigid joints** (`[Physics] rigid_joints`, on by default) — pistons and servo bearings hold the requested
  position exactly (a weld whose length/angle the mod sets): no sag, no wobble, a blocked piston just stops. **Zero gravity while the
  game splits the ship at launch** (`zero_gravity_on_launch`, ramped back afterwards). A piston stays one part with its load until it
  gets a target (`[Piston] whole_until_moved`) and the Turret Seat until someone sits and aims (`[Turret Seat] whole_until_aimed`).
  The piston's part and its load collide (`collide_with_load`). Mass and inertia are balanced across piston welds and the bearings
  next to them (`mass_ratio_max`), so heavy ships don't sag on light legs and cars don't sag on mini pistons. Servos accelerate and
  brake smoothly and limit the speed of the far end of what they turn (`[Bearing] servo_tip_speed`), so turning a big wing no
  longer tears it apart. Joints are created inside the physics step in which the game splits the parts (the first launch after
  loading no longer blows the ship apart); a piece that lands more than 0.5 m from its fit is left loose instead of welded there.
  `solver_iterations_rigid`. The mod's cost per frame roughly halved with a big ship. Pistons and bearings wired to a Wireless
  Transmitter read the channel from the sending transmitter. A chain of pistons and slip rings no longer doubles its power draw
  on every toggle (or burns the wires with the piston knob at 10%). New `[Multiplayer]` section.
- 0.25.0: **Turret Seat limit knobs** — three knobs on the front of the base set how far it aims left/right (up to a full circle), up and down (up to 135° each); default = as before (free / 135° up / 30° down); they also limit the whole-ship mode, and the values are kept per seat. `[Turret Seat] pitch_min` / `pitch_max` removed. Mini Piston, Mini Piston End, Mini Slip Ring and Mini Electric Thruster back in the Mechanics tab; the mirrored Bearing / Mini Bearing draw the angle switch in its place (it sat on the data port). Log files and folders in English (`logs/session-*.log`, `logs/marks`, `logs/_old`).
- 0.24.0: config with one section per kind of part (Mod Menu order) and dead keys removed; piston rails spread with the piston's length (`rails_per_meter`), a bent or stuck piston stops pushing (`max_tilt`), launch fit by time and progress (`fit_wait`) and `snap_back` to the garage pose; knob values no longer reset after an update; Turret Seat ports stay on the base; normal-seat key bindings work in third person (`[Seat] bindings_reach`); stuck Wireless Transmitters across a joint are repaired (`[Signal] fix_stuck_ports`); crash in the tutorial manual fixed; `[Bonus] extra_blocks`; `[Bearing] servo_gain`; `[Debug] trace`.
- 0.23.2: in-game descriptions follow the game's style (short; the long version is in `DESCRICOES_COMPLETAS.txt`), port tooltips trimmed, and every block shows **status lines** in the inventory panel (max force, power consumption, stroke, grip…) — the plugin fills `_customProperties` at run time from the Localization file, so it works in both languages.
- 0.23.1: the angle output kept its blue output plug (the pass-through painter was grabbing it, which also shifted the ring pairing by one); the switch has its own tooltip and its own saved value instead of the knob's; a pass-through port with no cable now shows the colour of the cable on the **other** side of the joint, like the Frame Quarter With Ports.
- 0.23.0: **angle output** on the Bearing, Gyro Bearing and the minis — the last port gives the angle the top is at, in degrees or on the same scale as the servo input, picked by a switch beside the knob. Pistons use 1 P/s per meter of the stroke they actually make (was a flat rate for the full 8 m).
- 0.22.7: cables can reach the pass-through ports on the sides of the turret seat's base (its collision volume no longer covers them).
- 0.22.0–0.22.6: **Slip Ring** and **Mini Slip Ring** — data and power cross a moving joint with no cable over it. Pass-through ports on the Bearing, Gyro Bearing, Piston, Recoupler (4), the minis (1) and the Turret Seat (4 per plate); their plugs take the colour of the cable plugged in. **Piston End** and **Mini Piston End** as plates; Piston 2 / 4 / 8 and the old cube Piston End removed. Motors glue only by the bottom of their base and the small fuel tanks only by their brackets; wheels, motors, small tanks and the turret seat take only the space of their shape. A bearing driven by a motor coasts at signal 0, and a motor's brake locks only the wheels it drives. New look for the Long Range Transmitter, the Shaft Transmitter and the Mini Solar Panel; the piston knob shows % and meters with no Piston End.
- 0.21.x: first try at carrying signals across a joint with the cable itself — replaced by the Slip Ring in 0.22.0.
- 0.20.1–0.20.5: Mini Bearings accept a Drive Shaft; a piston with a Piston End starts right on launch and survives a quick load.
- 0.20.0: single **Piston** (up to 8 m) instead of the 2 / 4 / 8 tiers (old ones hidden, still work) + **Piston End** block: placed on the piston's axis, the stroke becomes the distance to it and the rod is drawn extended already in the garage; with the game's H display on, a translucent guide bar shows the stroke, red on the piston nearest to a misaligned Piston End. **Mini Piston** (up to 4 m), **Mini Bearing** and **Mini Gyro Bearing** (0.25 m, one input, a quarter of the force). Knobs in the garage update every 0.1 s (was 0.5 s); the piston knob tooltip shows the stroke in meters when a Piston End is set.
- 0.19.15: knob values are kept per block (the game does not save them in the blueprint) and the knob tooltips also work in the garage.
- 0.19.14: **Gyro Bearing**; the mirrored (V) piston shows its rod again; the mirrored small battery's charge bar stays inside the block.
- 0.19.13: parts farther than 100 km from you stay simulated while they carry a powered Beacon (the game normally switches them off), and nothing is switched off during Replay playback.
- 0.19.12: diagnostic only — a transition log: whenever the game runs Core.CleanupWorldToDefaultState or Core.ClearGame (it happens on every atmosphere entry/exit), the state of every Long Range Transmitter, Wireless Transmitter, camera and monitor (ports, links, sources, power, slices, signals) is written before, right after, and 1 / 3 / 6 s later.
- 0.19.11: the Long Range Transmitter keeps passing its signal during Replay Mod playback (monitors fed through it showed static while a recording was playing).
- 0.19.10: port sources are followed through the cable itself (cell by cell) when the game's cached link points at a cable cell —
  after a time rewind load or a ship rebuild every Long Range Transmitter lost its channel/signal source at once; now the mod
  finds the other end on its own. Garage diagnostic line for pistons left extended.
- 0.19.9: Long Range Transmitter: the channel is also held when the path to its source disappears (leaving the atmosphere), not only
  when it reads 0; the log then shows which link of the path was lost and the block's state. Warning when a SENDING transmitter loses
  its signal source. The "not an LED" warning is logged once per block instead of every 2 s.
- 0.19.8: Long Range Transmitter LED: the prefab's LED renderer had alpha 0 and was born without a colour slot (nothing to paint);
  it now uses the game's indicator material and alpha 1, like the Wireless Transmitter (needs the bundle rebuilt). Log line when the
  video a transmitter sends stops changing (the camera on the other vehicle is not being simulated — out of the atmosphere / another planet).
- 0.19.7: Long Range Transmitter: the LED is the smallest coloured renderer of the block (the mesh reference did not tell them apart
  and the body was still painted green); the channel is held when its source momentarily reads 0 (leaving the seat, time rewind).
- 0.19.6: Long Range Transmitter: the signal copy and the channel read now run inside the physics tick (camera video no longer
  flickers at fast forward, the channel no longer drops to 0 for a few frames) and the channel only changes after it is stable
  for 0.3 s. The LED is recognised by the indicator mesh (learned from any wireless transmitter) — the body is never painted
  green/red again. Pistons left extended out of the simulation are retracted (re-checked every 0.5 s). Turret seat, whole-ship
  mode: aim and heading are continuous — turning right past 180° keeps turning right.
- 0.19.5: radar: the beacon's icon entities were leaking every time you went back to the garage (the game then counted them as its
  own icons: all slots "in use", duplicated/stale icons on the screen) — orphans are now destroyed. Pistons left extended in the garage
  after a simulation are pulled back to rest when rediscovered.
- 0.19.4: crash fix — the crash dumps (LocalDumps) showed an access violation in the Long Range Transmitter code reading a port
  entity the game had already destroyed (it recreates the blueprint's entities when you place a block in the garage, and on unload).
  The cached ports are re-found when they die; every raw read checks the entity first. Dead transmitters are skipped in the LED loop.
- 0.19.3: radar: beacon icons no longer stay on the screen after the simulation ends, and the icon position formula was wrong
  (always pushed to the edge) — now checked against the base icon and logged. Transmitter LEDs actually light (the renderer colour
  is packed; the LED is found through the game's indicator entity / the alpha-0 renderer). Bearing/piston knob now points out of the
  panel (the game overwrites the knob's rotation, so the knob spins around Z) and its tooltip works in the garage too. Steering
  Wheel per Andre's drawing: column rising from the centre of the box's top face, tilted towards the player, wheel on top, ONE output
  on the back face. Colliders follow the model: one convex hull per part (boxes, cylinders, arcs), concave parts split — cables, sensors
  and logic blocks fit in the gaps (also pedal, steering wheel, single cell battery). See-through faces fixed: port pockets on the
  steering wheel, reverser, pistons, electric and fuel motors and differential; tyre/rim gap on all wheels; pedal wedge/box gap.
  Turret seat z-fighting (sleeve collar, hub flanges) fixed and corner plates seated. Mini Electric Thruster reverse switch pulled out.
- 0.19.2: beacon icons are written straight into the radar's icon buffer (they also show in Replay playback). Status LED on both
  transmitters (green = linked, yellow = more than one sender on the channel, red = nobody on the other end); the Long Range
  Transmitter is dark grey like the Shaft Transmitter. Knob on the Bearing (servo angle, 10–360°) and on the Pistons (stroke
  limit, 10–100%), with tooltips; mirrored Beacon. Convex colliders built from the models (cables pass the corners of bearings,
  motors, pistons and the recoupler). Steering Wheel redesigned (low 2x1x2 box, wheel entering through a socket, outputs on both
  sides); pedal output in the corner cell; turret seat details; bolt heads no longer see-through; Mini Electric Thruster plugs
  stick out of the body. Crash guard: the mod pauses while the game unloads the world.
- 0.19.0: Mini Solar Panel (Frame Quarter size, 1/8 of the solar panel). Mirrored (V) versions of the Single Cell Battery, Long
  Range Transmitter and Mini Solar Panel. Mini Electric Thruster ports moved off the bottom to the lower corner, on two neighbouring
  sides. The transmitters' inventory icons show the channel number. The Long Range Transmitter passes camera video. Step-by-step log
  right after a blueprint is loaded (to find a crash).
- 0.18.2: the Long Range Transmitter's screen shows its channel (the number arriving on the channel input).
- 0.18.1: Mini Electric Thruster with a box collider of its own size (the game's mesh collider ignored the half scale); Long Range
  Transmitter with the flat screen and the editable label of the Wireless Transmitter.
- 0.18.0: Single Cell Battery, Mini Electric Thruster and Long Range Transmitter.
- 0.17.3: `[Beacon] hide_on_own_vehicle` (false = the beacon never hides itself; the radar can't hide it from just one radar, so
  with the Replay free camera it used to vanish everywhere). Radar icons re-checked when the simulation starts/ends.
- 0.17.1: Drive Shaft / Shaft Transmitter get their color right when placed (the renderers now follow the painted color);
  the channel number shows on the Shaft Transmitter (text material relinked); the beacon's knob is clickable; bearing ports
  are half orange, half purple; the beacon's radar icons only go over game icons nothing is using right now (re-checked on
  load, every 30 s and when the simulation starts or ends).
- 0.17.0: Drive Shafts pass through Frames With Ports; inputs/outputs are enforced; several motors on the same shaft add up
  (Differential now has 4 inputs); opposite signals lock the bearings (red LED). Drive Shaft and Shaft Transmitter in dark gray.
  Beacon: knob + display to pick its radar icon (signal waves, car, ship). The beacon no longer has a mirrored version.
- 0.16.0: **Shaft Transmitter** (wireless Drive Shaft, a copy of the game's Wireless Transmitter with its own channels) and colored
  shaft ports: orange = input, purple = output.
- 0.15.0: **Drive Shaft**, **Differential** and **Shaft Reverser**. The motors' output is now a shaft port (connect it with a
  Drive Shaft, not a data cable), and the bearing's 4 ports have no type: Drive Shaft = motor, data = servo (both: the shaft wins
  while it turns). The force is split among the bearings reached through the shafts. Blueprints made with 0.14 or older need the
  data cables between motors and bearings replaced by Drive Shafts.
- 0.14.0: a motor's force is now **shared** among the bearings it drives, and every motor got 4x the force (`[Motor] force_pool`):
  one motor per bearing = 4x stronger; one motor for 4 bearings = the same as before (old blueprints don't get weaker). Top speed
  is unchanged (it comes from the motor size and the wheel diameter).
- 0.13.2: turret seat — seat key bindings (Space/Shift bound to the car or ship) keep working when you aim backwards: their reach is
  measured from the nearest of the game's own seated view and the turret's forward view, instead of your turning view.
- 0.13.1: the mirrored versions (" M") no longer show up as duplicates in the inventory (V still swaps to them). Works with the renamed
  library (Mod Menu).
- 0.13.0: English release; mirror with V for every asymmetric part; wheels attach only by the flange side; turn arrows on the bearing;
  debug keys off by default; the game's own meshes are no longer shipped in the bundle (linked at runtime).
