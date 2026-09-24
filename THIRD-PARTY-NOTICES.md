# Third-party notices

## BepuPhysics2 (upstream demos and library)

The demo sources under `Temp/` and the physics API consumed by `src/Game.BepuDemos` come from
BepuPhysics2, Copyright (c) Ross Nordby, licensed under the Apache License 2.0.

- https://github.com/bepu/bepuphysics2

The `Bonobo.Bepuphysics2` NuGet package is a namespace-rebranded build of BepuPhysics2
(same license, Apache-2.0).

## Vendored demo assets

`src/Game.BepuDemos/Content/newt.obj`, `src/BepuDemos.UI/public/models/newt.obj` and the 27
`src/BepuDemos.UI/public/sponsors/*.png` images are copied from the upstream BepuPhysics2 demo
content (`Temp/Demos/Content/`), Copyright (c) Ross Nordby, Apache-2.0. The sponsor images are
fan/backer reward art from the upstream demo and are reproduced here only as part of the ported
demo fixture.

## Bonobo engine (host/pattern reference)

`src/DemoEngine` mirrors the Bonobo engine's zero-copy host patterns
(`PinnedRenderBuffer`, signal header/layout, fixed-step host loop, WebView2 shared-buffer
channels, loopback asset server). Bonobo engine sources are MIT licensed:

- Copyright (c) 2026 Jj Cañon — https://github.com/josejaviercanon/bonoboengine.wasm.3D

## Bonobo.ECS / Bonobo.ECS.SourceGenerators

MIT License, Copyright (c) 2026 Jj Cañon — https://github.com/josejaviercanon/Bonobo-ECS

## Babylon.js

Babylon.js v9 (Apache-2.0) — https://www.babylonjs.com
