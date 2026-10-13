**English** | [简体中文](README.zh-CN.md)

# PLSea

Real-time ocean and volumetric clouds in Unity HDRP. All shaders and compute shaders are written from scratch, with no paid plugins.

- **PLOcean** — FFT waves, interactive ripples, underwater lighting, caustics, buoyancy
- **PLCloud** — Nubis-style volumetric clouds that fill the sky and cast shadows

Both systems share one planet radius, so the distant sea curves below the horizon.

Built with Unity 2022.3 + HDRP 14.

<img src="https://github.com/PuLei36/notes/blob/note/images/SeaSample.png?raw=true" alt="Alt Text" style="zoom: 50%;" />

## Ocean

**Waves**: JONSWAP spectrum with directional spreading, IFFT on the GPU every frame. Three cascades are split by wavenumber (250 m / 17 m / 5 m) with no overlap, so no wavelength is summed twice. Horizontal displacement sharpens the crests, and the accumulated Jacobian drives the foam.

**Mesh**: a quadtree LOD that gets denser toward the camera, so LOD boundaries form concentric rings. It is 2:1 balanced with midpoints inserted along coarse-to-fine edges, so there are no T-junctions. The mesh follows the camera snapped to whole cells, which keeps vertices from swimming as you move.

**Lighting** (water BSDF modelled after HPWater):
- Macro volumetric scattering marches exponentially along the refracted ray, sampling the sun shadow at each step, which is what produces the underwater god rays
- Subsurface scattering through thin wave crests, plus backlit transmission
- Refraction can ray-march with exponential steps, blurred by mip level and dispersed according to the underwater path length
- Reflection falls back through SSR → reflection probes → sky by hierarchy weight
- Caustics come from photons traced in a compute shader, sampled across three depth slices

**Interactive ripples**: a 2D wave equation solved over a region around the camera, with fixed 60 Hz substeps and a sponge layer at the borders. A top-down scene height pass marks anything above the waterline as an obstacle (waves reflect off it) and damps waves in shallow water. Objects moving through the surface generate ripples automatically; you can also attach `PLOceanRippleSource` or call `AddImpulse`.

**Buoyancy**: pressure integrated over hull cross-sections. Surface height comes from an async readback of the FFT displacement textures, so it lags the image by 1 to 3 frames. The surface inside a hull is masked out, so water does not flood the boat.

**Underwater**: the waterline is evaluated per pixel, so it rides the waves and tilts with the camera. Below it you get volumetric fog, god rays, and caustics, plus a thin water film right at the line.

## Volumetric clouds

The cloud layer is a spherical shell that follows the planet's curvature. Its altitude range is set on the material and it extends infinitely in the horizontal plane, so it reaches all the way to the horizon. The density field clumps with Perlin-Worley noise and erodes with Worley FBM, while a weather map drives coverage and cloud-top height.

Lighting uses a dual-lobe Henyey-Greenstein phase function plus a three-octave multiple-scattering approximation, with a powder term. Sunlight is HDRP's main directional light multiplied by PBR atmospheric transmittance; ambient comes from the ambient probe. Distant clouds blend toward the sky color so they settle naturally into the haze at the horizon.

Performance follows the Nubis approach: march at half resolution with a per-frame jitter offset, reproject the previous frame by cloud depth for temporal accumulation, then composite with depth-aware upsampling. Step size grows with distance, empty space is skipped with cheap samples and large strides, and noise mip level is chosen by distance. Roughly 3 to 10 ms at 1080p (RTX 3060 Laptop, rough measurement).

Cloud shadows are rendered as a transmittance map bound to the directional light's cookie, so every object and the volumetric fog receive them automatically.

## Getting started

The repository ships no scene, so you need to build one.

1. Open the project in Unity 2022.3 and set the render pipeline asset in Project Settings to `Assets/Settings/HDRP High Fidelity.asset`.
2. Run `Tools/PLCloud/Generate Cloud Noise 128` to generate the cloud 3D noise (one is already committed; regenerating is fine).
3. Create a scene and add:
   - An empty object with `SeaMesh` + `PLOceanSimulation`, with `Sea Material` set to `PLOcean/Materials/PLOceanSea.mat`. The object's Y is the sea level.
   - An empty object with `PLOceanRipple` (interactive ripples, optional).
   - An empty object with `PLCloudVolume`, `Material` set to `PLCloud/Materials/PLCloud.mat` and `Sun` set to your directional light. On the same object add a `CustomPassVolume` at injection point `Before Post Process` containing `PLCloudPass`.
   - Another `CustomPassVolume`: `PLOceanVolumePass` at `Before Transparent` and `PLOceanUnderseaPass` at `Before Post Process`, both with `Sea Material` set to the water material.
   - A Volume using `Assets/Settings/OutdoorsSkyProfile.asset` (Physically Based Sky + fog).
4. For planet curvature, set `SeaMesh`'s `Planet Radius` and Physically Based Sky's `Planetary Radius` to the same value (6378100 by default). Use 0 for a flat sea. The clouds follow this value automatically.
5. To reach the horizon, set `SeaMesh`'s `LOD Levels` to 12 and `Outer Radius` to 150000, and push the camera's far clip plane out to 160 km.

### Third-party assets

The following are excluded for licensing reasons. Import them from the Asset Store yourself, or substitute your own:

- NatureManufacture terrain and vegetation
- Atlantis Ruins
- Quantum Stage (character)

The character controller in `Assets/Scripts` needs a Humanoid rig with an Animator; the animator controller is at `Assets/Player/PlayerAnimator.controller`.

## Parameter reference

| Where | Parameter | Effect |
| --- | --- | --- |
| PLOceanSimulation | Wind Speed / Fetch | Wave size and maturity |
| | Choppiness | How sharp the crests get |
| | Length Scale 0/1/2 | Tiling size of the three cascades |
| | Caustics | Caustics toggle and parameters |
| SeaMesh | Base Cell Size / Inner Radius / LOD Levels | Mesh density and extent |
| | Planet Radius | Planet curvature; 0 is flat |
| PLOceanSea.mat | Scatter / Absorption | Water color |
| | Reflection / Specular Intensity | Reflection and highlight strength |
| | Undersea Fog Density / God Rays | Underwater fog and light shafts |
| PLCloud.mat | Cloud Bottom / Top Altitude | Cloud layer altitude range |
| | Coverage / Coverage Variation | Cloud amount and how it varies |
| | Extinction | How solid the clouds look |
| | Max View Steps / Light Steps | Quality versus performance |
| PLCloudPass | Downsample / History Weight | Cloud render resolution and temporal accumulation |

## References

- Tessendorf, *Simulating Ocean Water* — FFT waves
- Hillaire, *Physically Based and Unified Volumetric Rendering in Frostbite* (2015) — energy-conserving volumetric integration
- Schneider, *The Real-time Volumetric Cloudscapes of Horizon Zero Dawn* (2015) and *Nubis³* (Advances in Real-Time Rendering, 2023) — volumetric clouds
- Wrenninge et al., *Oz: The Great and Volumetric* (2013) — multiple-scattering approximation
- Hillaire, *A Scalable and Production Ready Sky and Atmosphere Rendering Technique* (2020) — atmosphere
