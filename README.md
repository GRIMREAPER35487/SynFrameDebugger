# Synthos Frame Debugger Exporter

A powerful Unity Editor profiling tool that inspects and exports Unity Frame Debugger event hierarchies, draw calls, shader parameters, render target bindings, and batch break causes into rich, structured JSON files for in-depth analysis.

## Features

- **Deep Reflection Capture:** Accesses Unity's internal `FrameDebuggerUtility` and `FrameDebuggerEventData` to extract comprehensive draw call diagnostics that Unity normally does not expose to scripting.
- **Batch Break Diagnostics:** Automatically records batch break causes (different shaders, material properties, lightmaps, multipass shaders, etc.) to help identify rendering bottlenecks.
- **Shader & Material Analysis:** Captures per-draw call shaders, keywords, stencil states, blend modes, and vertex counts.
- **Asynchronous Safe Export:** Iterates frame event streams smoothly with an interactive progress bar and cancellation support.

## How to Open

You can launch the tool inside the Unity Editor via:
- **Unity Top Menu:** `Window > Synthos > Syn Frame Exporter`
- **Tools Menu:** `Tools > Synthos > Syn Frame Exporter`

## Installation via VPM (VRChat Creator Companion / ALCOM)

Add the Synthos package repository to your Creator Companion or ALCOM:
```
https://grimreaper35487.github.io/Synthos-VRC-Packages/index.json
```
Then search for **Synthos Frame Debugger Exporter** and click **Install**.

## License

MIT License - Copyright (c) 2026 Synthos.
