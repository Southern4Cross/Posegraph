# Posegraph

**English** | [简体中文](README.zh-CN.md)

Turn a photo, an illustration, or a conversation into a pose for your FINAL FANTASY XIV character.

Posegraph is a Dalamud plugin with a local AI backend. Pick an image in game, and the backend recognizes the
3D body pose with Meta's [SAM 3D Body](https://github.com/facebookresearch/sam-3d-body), retargets it onto the
FFXIV skeleton of your race, and the plugin applies it to your GPose actor through [Brio](https://github.com/Etheirys/Brio).
You can also describe a pose in chat, or refine an imported pose over several messages, using your own model API.

```
Posegraph plugin (in game, ImGui)  ── HTTP (127.0.0.1) ──►  Posegraph backend (local Python)
  status, image picker, overlays,                            SAM 3D Body recognition (once per image)
  3D preview, tuning sliders,                                MHR → FFXIV retargeting (~50 ms per change)
  apply / camera / export          ◄── Brio pose JSON ───   race skeletons, plausibility checks
        │
        └── Brio IPC: freeze actor, load pose
```

## Features

- **Photo and illustration support**, full or half body, fingers included. An *Illustration* preset tones down
  the exaggerated head/neck proportions of drawn art.
- **All playable male races.** The race is read from the character's appearance (or chosen manually).
  Midlander-type and Elezen males are verified in game; Lalafell and Roegadyn/Hrothgar had a body-proportion bug after changing race, fixed in 0.3.2.
  Female skeletons are **not supported yet** (their data is included, but the retargeting rules are tuned for male skeletons).
- **Live tuning without re-running the AI**: per-region amplitude (spine, neck, head, clavicle, wrist), hand contact
  compensation, gravity correction, ground snapping, helper-bone strength, and manual per-joint offsets in a 3D stick figure.
  Dragging a slider updates the preview; releasing it applies the pose (Brio's undo history stays clean).
- **Plausibility hints** with one-click fixes, based on ranges sampled from in-game animations.
- **Manual AI pose advice**: analyze the current preview or actor pose from the *Hints* tab, using bone-to-body mappings,
  joint relationships and rule hints. Analysis leaves the pose unchanged.
- **Hand contact compensation**: hands touching the head / chest / hips stay in contact even when your race's
  proportions differ from the person in the photo; elbows stay pure hinges.
- **Camera matching**: reproduces the photo's camera angle, distance and field of view in GPose.
- **Export** Brio `.pose` files.
- **Pose chat**: create a pose from text or adjust the current image-derived pose through multiple messages.
  Configure an OpenAI-compatible API endpoint, key and model in Settings. Text models work with the skeleton;
  sending the reference image is optional and requires a model that accepts images.
- **Low-VRAM friendly**: Auto / GPU / CPU modes, the model is unloaded right after each recognition by default,
  and CPU mode uses no VRAM at all.
- **English and Simplified Chinese UI**, following the game client language by default.

## Requirements

- FINAL FANTASY XIV with Dalamud (API 15) and **Brio** installed and enabled
- Windows 10/11 64-bit
- For image recognition on the GPU: an NVIDIA GPU with about **3 GB free VRAM** (about 2 GB used while recognizing).
  Without it, Posegraph runs on the CPU: about 10–20 s per image and about 4 GB RAM.
- About 7 GB of disk space for the backend package
- For pose chat: an OpenAI-compatible chat-completions service, hosted or local, and its model name and credentials.
  Text-only chat uses the local backend but does not load SAM 3D Body or require its weights, PyTorch or a GPU.
  A lightweight source installation is described below.

## Installation

### 1. Backend package

1. Download **[Posegraph-Backend-win64.zip](https://drive.google.com/file/d/1PnbqdyH0hOTMSPhBaSXQ-Dpjp3fil6k2/view?usp=sharing)** (Google Drive, about 4 GB) and extract it somewhere with
   enough space. An ASCII-only path is recommended.
2. The folder contains `python`, `backend`, `LICENSES`, `Start-Backend.bat` and `README.txt`. Nothing needs to be installed.

### 2. Plugin

Posegraph is not in the official Dalamud repository; install it from the custom repository:

1. In game: `/xlsettings` → *Experimental* → *Custom Plugin Repositories*, add
   `https://raw.githubusercontent.com/extrant/DalamudPlugins/main/pluginmaster.json`, tick it and save.
2. Search for **Posegraph** in the plugin installer and install it.

Alternatively, download `Posegraph.zip` from the [Releases page](https://github.com/extrant/Posegraph/releases), extract it
and add `Posegraph.dll` under *Dev Plugin Locations*.

### 3. First start

1. `/posegraph` opens the main window.
2. *Settings* → **Choose backend package folder…** → select the extracted folder.
3. Click the **plug** icon in the status bar to start the backend. Optionally enable *Start the backend when the plugin loads*.

### Updating

- **Plugin**: updates automatically through the plugin repository.
- **Backend (package users)**: after the plugin updates, *Settings* shows "Backend …, plugin …". When the backend is older,
  click the download button next to it. The plugin downloads the matching source from this repository's tag
  (`archive/refs/tags/vX.Y.Z.zip`, under 1 MB) and replaces only `backend\src`; Python, dependencies and the model stay.
  The previous version is kept as `backend\src.bak` and restored automatically if the new one fails to start.
  If GitHub is unreachable, set a mirror prefix such as `https://gh-proxy.org/`.
- A full package download is only needed when the dependencies change; the plugin tells you
  (the package's `runtime.json` is older than what the new backend requires).
- Source installs: update with `git pull` / checkout of the same tag.

## Usage

1. Enter GPose and select the actor (or pick one in the actor list, which shows players, actors created in Brio
   and humanoid NPCs such as retainers).
2. Click the folder icon and choose **Choose images (multiple)** or **Choose folder (including subfolders)**.
   You can also drop images or folders onto the *Image* tab (jpg, png, webp, bmp, gif first frame, tiff, avif).
   The adjacent clipboard button (**Recognize the image in the clipboard**, Chinese tooltip: **识别剪切板中的图像**)
   imports a copied image directly.
3. The first recognition loads the model (about 10–20 s); after that a recognition takes a few seconds.
   For multiple images, Posegraph pre-recognizes the batch in the background. As each image finishes, click its successful
   row in the left-hand list to load its preview and pose while the remaining images continue recognizing.
   Only the image you select loads; its application follows the existing *Apply to actor right after recognition* setting.
   A single image loads automatically.
4. Check the overlay (*Image* tab: green = detected skeleton, orange = FFXIV skeleton) and the *3D* tab.
5. Tune on the *Tuning* tab, read the *Hints* tab, then **Apply** (▶). Use the camera button to match the photo camera,
   ↶ to restore the pose from before the first apply, and the disk button to export a `.pose` file.

Update the backend together with the plugin when importing more than eight images. The backend keeps up to eight recognized
sessions in RAM and stores additional sessions in temporary disk files, so earlier results remain available for selection.
Replacing the imported batch or unloading the plugin clears its cached sessions and temporary files.

Multi-image recognition is sequential by default (**1**). In *Settings* → *Recognition backend* →
**Multi-image recognition concurrency**, choose **1–8** from the dropdown; changes take effect with the next batch.
Settings above **1** require an updated, restarted backend. If the running backend cannot confirm support, Posegraph
reports an error and does not start the batch; the default **1** remains compatible with older backends.
Parallel recognition uses isolated workers, each loading a complete model, so RAM and VRAM requirements grow with the
worker count. The estimates to the right of the dropdown budget approximately **3 GB VRAM per GPU worker** (plus
**0.75 GB** if MHR also runs on the GPU) and **4 GB RAM per worker**. CPU recognition has no inference VRAM budget;
*Auto* shows the GPU plan and may fall back to CPU. Actual extra memory depends on the mode and images; estimates exclude
the game, image and session caches, and residual GPU context. Higher concurrency may not improve speed; parallel-mode performance
and resource use have not yet been validated on real hardware. **Stop pre-recognition** lets in-flight images finish
and skips images that have not started.

Status bar icons, left to right: Brio, GPose, IVCS (shown once the game pose has been read), backend, VRAM, RAM;
hover for details.

### Pose chat

1. Open *Settings* → *Prompt poses*, enter your API endpoint and key, click *Fetch models*, then select a model from
   the dropdown. The selection is saved automatically. Use an OpenAI-compatible base URL (for example
   `https://your-provider.example/v1`) or the complete `/chat/completions` URL. A local service that needs no
   authentication may use an empty key. The plugin fetches models directly; the local backend need not be running.
2. Start the local backend and open the *Chat* tab. Import an image first to adjust its recognized pose, or start without an image to create a pose
   from the current character pose. Use *Start from standing pose* to create a pose from scratch.
3. Send a description such as “Raise the left hand to shoulder height and look to the right.” Continue with
   “A little higher, and relax the other arm.” Each turn uses the current skeleton and the recent conversation.
4. Click *Generate preview*, inspect the *3D* preview and *Hints*, then click **Apply** to update the actor, or export a `.pose` file.
   Chat replies update the preview; applying to the game remains an explicit action.

With an actor selected in GPose, chat reads the actor's complete loaded rig, parent hierarchy and neutral local transforms,
including facial, hair and accessory bones. For example, “Open the eyes and keep everything else unchanged” restores only
the eyelids, preserving the body, gaze and other expressions. Missing facial data in an image-derived preview is filled in
from the current actor. When a loaded rig is unavailable, the model receives an explicit capability description.

An ordinary text model is sufficient: the backend supplies structured skeleton context and validates the model's
bone edits locally. Enable *Send reference image* in the *Chat* tab when the model supports image input and visual context
is useful. Images are compressed to JPEG before sending, with a maximum edge of 1024 pixels and a 256 KB file limit;
the local source image and preview stay unchanged. With that option off, the image stays local; prompts, recent conversation and skeleton data are sent to
the configured service. Image recognition itself still runs locally through SAM 3D Body.

The API key is masked in the settings UI and stored in the plugin configuration using Windows encryption for the current user.
The backend uses the credentials for the request without saving them.

Use *New conversation* to clear the dialogue while retaining the current pose. *Start from standing pose* clears both.
Starting from standing also detaches the previous image for subsequent turns; import an image or return to the image pose to use it again.
Importing another image or changing actors also clears context so
requests do not carry instructions from another pose. Image-tuning controls and photo-camera matching apply to the
recognized image pose; use *Return to image pose* when you want to use them again. *Cancel generation* discards an
in-flight reply without changing the current pose; the provider may still finish processing that request.

### AI pose advice

1. Configure a model as described under *Pose chat*, start the local backend, then open the *Hints* tab.
2. Optionally enter a focus such as “Keep the left hand near the head; check whether the shoulders and neck look natural.”
   Click **Analyze current pose with AI** to send a request to the configured model service. Opening the tab, moving sliders
   and refreshing rule hints do not call the model. Analysis is also available when no rule warnings are present.
3. The model receives the current preview pose (or the selected actor's pose when there is no preview), race, loaded bone
   hierarchy, bone-to-body labels, coordinate conventions, joint transforms and measured relationships between the hands,
   feet, head, chest and pelvis. Existing rule hints and applicable image-tuning parameters are included as well.
4. Review each issue, its evidence and the recommended adjustment. When a suggestion identifies a locatable joint, you can
   inspect it in *3D*. You can also transfer a suggestion to the *Chat* input, then explicitly click *Generate preview* and
   inspect the result. Analysis itself does not change the preview, actor pose or chat history.

Reference-image sending for advice is a separate opt-in, off by default; it does not inherit the *Chat* tab's setting.
It requires a model that accepts images and sends the compressed imported reference, **not a screenshot of the game actor**.
Skeleton data and the reference image cannot confirm clothing clipping, skin deformation or exact physical contact.

Advice applies to the pose captured when analysis started. Changing the actor, image, race or pose invalidates previous
suggestions; click the analysis button again to refresh them. You can cancel a pending analysis, although the model provider
may still finish a request it has already received.

### Settings

| Setting | Default | Notes |
|---|---|---|
| Language | Auto | Chinese if the game client is Chinese, otherwise the Dalamud UI language decides; English if neither is Chinese |
| Device | Auto | GPU when ~3 GB VRAM is free, otherwise CPU. *GPU* forces the GPU, *CPU* never touches VRAM |
| Unload model | Right after recognition | Frees VRAM/RAM for the game; the next recognition reloads the model (~5–10 s) |
| Keep MHR on the CPU | On | Saves ~750 MB VRAM in GPU mode, ~0.5 s slower |
| Apply when a slider is released | On | Turn off to apply manually |
| Chat API endpoint / API key / model | Empty | Enter the endpoint and key, fetch models, then select from the dropdown; the selection is saved automatically. Leave the key empty when authentication is not required |
| Send reference image | Off | Requires an imported image and a model that accepts images; sends the preview image to the configured service |

### Races

| Skeleton group | Races | Status |
|---|---|---|
| Midlander-type ♂ | Midlander, Highlander, Miqo'te, Au Ra, Viera | verified in game |
| Elezen ♂ | Elezen | verified in game |
| Roegadyn-type ♂ | Roegadyn, Hrothgar | fixed in 0.3.2, awaiting a retest in game |
| Lalafell | Lalafell ♂ / ♀ | fixed in 0.3.2, awaiting a retest in game |
| Female skeletons | all | **not supported yet** |

If the detected race is wrong (race-changing mods, Customize+ scaling), choose it manually in the race list next to the actor list.
The choice is remembered per actor for the GPose session; switching between actors does not clear their poses in Brio.

The skeleton data comes from IVCS. Actors without the IVCS skeleton work too: the bend of each finger's third joint is
merged into the second, and the IVCS icon in the status bar turns yellow.

## Known limitations

- Single-image depth is ambiguous (e.g. a hand in front of or behind the head).
- No collision avoidance yet: Lalafell's raised arms can pass through the head, and Roegadyn/Hrothgar thighs can
  sink into the belly in high-knee poses.
- Very strong elbow flexion creases more than in-game animations (~145° vs ~130°).
- Jumping / airborne images are pulled to the ground unless *Snap to ground* is turned off.
- Multiple people and video are reserved in the UI but not implemented; the whole image is treated as one person.
- The CPU mode speed depends heavily on the CPU (measured ~10 s per image on a 20-thread laptop CPU).
- Pose-chat accuracy depends on the configured model. Bone-edit validation prevents malformed output from being applied;
  it does not guarantee anatomical accuracy, contact or collision avoidance. Inspect the preview before applying.
- Chat generates a static pose. It does not generate an animation sequence, and image-camera matching is unavailable
  for a pose created from text.

## Building from source

### Plugin

```powershell
cd Posegraph
dotnet build -c Release      # output: Posegraph\bin\Release\Posegraph\latest.zip
```

The project uses `Dalamud.NET.SDK` 15 and `Brio.API`. For XIVLauncherCN the Dalamud path is picked up automatically.

### Backend (development install)

```powershell
cd backend
powershell -ExecutionPolicy Bypass -File scripts\setup_sam3d.ps1     # venv, CUDA PyTorch, upstream source, weights (SHA256-checked)
.venv\Scripts\python -m posegraph.app                               # http://127.0.0.1:42430
```

Weights come from ModelScope by default (no login); `-Source hf` uses the official gated Hugging Face repository.
Environment variables (prefix `POSEGRAPH_`): `DEVICE` (auto|cuda|cpu), `PRECISION` (auto|bf16|fp16|fp32), `MHR_ON_CPU`,
`MIN_FREE_VRAM_MB`, `RELEASE_AFTER` (seconds, 0 = right away, -1 = keep), `RECOGNITION_PARALLELISM` (1–8, default 1), `PRELOAD`, `SAVE_SESSIONS` (save each recognition as an npz under `outputs/ui`), `PORT`, `BRIDGE_URL`, `SAM3D_REPO`, `SAM3D_CKPT`.

For **text-only pose chat**, install just the backend's base dependencies with Python 3.11:

```powershell
cd backend
py -3.11 -m venv .venv
.venv\Scripts\python -m pip install -e .
.venv\Scripts\python -m posegraph.app
```

In the plugin's advanced backend settings, point Python to this environment's `python.exe` and the backend folder
to this repository's `backend` directory. Leave model preloading off. Image recognition needs the full setup above.

### Regression checks

From the repository root, after installing the backend's base dependencies and the .NET SDK:

```powershell
backend\.venv\Scripts\python -m unittest discover -s backend\tests -v
Get-ChildItem tests -Recurse -Filter *.csproj | ForEach-Object {
    dotnet run --project $_.FullName -c Release
    if ($LASTEXITCODE -ne 0) { throw "Check failed: $($_.Name)" }
}
dotnet build Posegraph\Posegraph.csproj -c Release
```

Building the plugin also requires the local Dalamud development libraries. The checks exercise production sources with
synthetic poses, a local HTTP service and isolated test workers; they do not require a paid model API or the game.
Real SAM 3D Body concurrency, GPU memory use, native actor-rig access and in-game clipboard/Brio behavior need separate live validation.

### Backend package

```powershell
powershell -ExecutionPolicy Bypass -File packaging\build_backend_package.ps1 `
    -Python C:\Path\To\Python311 -Venv backend\.venv `
    -Sam3d backend\third_party\sam-3d-body -Weights backend\checkpoints\sam-3d-body-vith `
    -Out D:\dist -Zip
```

It copies a regular CPython 3.11 install as a portable interpreter, the venv's site-packages, the backend source,
the SAM 3D Body source and weights, and the license files.

### Developer tools (`backend/scripts`, Chinese output)

| Script | Purpose |
|---|---|
| `photo_to_game.py`, `image_to_pose.py`, `push_pose.py`, `preview_overlay.py` | Command-line pipeline without the plugin UI |
| `verify_devices.py`, `bench_sam3d.py` | Compare CPU/GPU outputs, measure VRAM and speed (see `docs/benchmark.md`) |
| `build_retarget_data.py` | Regenerate skeleton data from IVCS `.sklb` files |
| `sample_frames.py` | Sample in-game animation frames (used to fit helper-bone rules) |
| `export_race_compare.py`, `blender_race_compare.py`, `build_body_extents.py` | Multi-race comparison scenes in Blender and body-size tables |

## HTTP interfaces

The plugin also runs a small local server (127.0.0.1:42429) used by the backend for camera matching and by the
command-line tools: `GET /status`, `GET /actors`, `GET|POST /pose?actor=N`, `GET /rig?actor=N`, `GET|POST /camera`.
The backend (127.0.0.1:42430) exposes `GET /health`, `POST /sessions`, `GET /sessions/{id}/image`, `DELETE /sessions/{id}`,
`POST /sessions/{id}/retarget`, `POST /sessions/{id}/camera`, `POST /model/load|unload|config`, `GET /races`, `GET /presets`,
`POST /chat/pose`, `POST /chat/advice`, `POST /shutdown`. Both listen on localhost only.

`POST /model/config` accepts `recognition_parallelism` (integer 1–8) and reports the effective setting as
`model.config.recognitionParallelism`. Changes wait for active inference, then release old workers before taking effect.

`POST /chat/pose` takes `prompt`, `history` (`user`/`assistant` messages), the current Brio `pose` (optional), `race`,
`ground`, optional `session_id`/`include_image`, optional loaded `rig` and `actor_pose`, and `provider` (`endpoint`, `api_key`, `model`). It returns `reply`,
`history` and `result` in the same pose/preview format as retargeting. The caller retains conversation history and
passes it with the current pose on the next turn. No image session or recognition-model load is needed for text-only requests.

`POST /chat/advice` accepts the current `pose`, `race`, optional `rig`, `actor_pose`, `params`, `warnings`, `focus`,
optional `session_id`/`include_image`, and the same `provider`. It returns structured observations and suggestions;
it does not edit or apply a pose. Both chat endpoints keep credentials and conversation state out of server-side storage.

## License

Posegraph's own code (plugin, backend, scripts) is licensed under the [MIT License](LICENSE)
(versions up to 0.3.8 were released under the PolyForm Noncommercial License 1.0.0).
Third-party components keep their own licenses; in particular, the SAM 3D Body code and weights shipped in the
backend package are under Meta's SAM License.

## Credits

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). SAM 3D Body (code and weights) is © Meta and released under the SAM License;
the FFXIV skeleton data is extracted from Illusio Vitae Custom Skeletons.

FINAL FANTASY XIV © SQUARE ENIX CO., LTD. Posegraph is a fan-made tool, not affiliated with or endorsed by Square Enix.
