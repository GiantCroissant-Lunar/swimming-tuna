---
name: remotion-best-practices
description: Best practices for Remotion - Video creation in React. Use this skill whenever dealing with Remotion code for video creation, animations, compositions, and rendering.
metadata:
  tags: remotion, video, react, animation, composition
---

# Remotion Best Practices

Best practices for Remotion - Video creation in React.

## When to use

Use this skill whenever you are dealing with Remotion code to obtain the domain-specific knowledge for video creation, animations, compositions, and rendering.

## Overview

Remotion is a framework for creating videos programmatically using React. It allows you to:
- Write videos in React using familiar web technologies
- Use TypeScript for type-safe video development
- Leverage the full React ecosystem
- Render videos programmatically or via UI

## Quick Reference

### Core Concepts

- **Composition**: A component that defines a video sequence with dimensions, duration, and FPS
- **Sequence**: A component for timing - delays, trimming, limiting duration
- **useCurrentFrame**: Hook to get the current frame number for animations
- **useVideoConfig**: Hook to access composition configuration (width, height, FPS, durationInFrames)
- **interpolate**: Function to map values between ranges for animations
- **spring**: Physics-based spring animations
- **Easing**: Interpolation curves (linear, ease, etc.)

### Available Rules

Read individual rule files for detailed explanations and code examples:

- [rules/3d.md](rules/3d.md) - 3D content using Three.js and React Three Fiber
- [rules/animations.md](rules/animations.md) - Fundamental animation skills
- [rules/assets.md](rules/assets.md) - Importing images, videos, audio, and fonts
- [rules/audio.md](rules/audio.md) - Using audio and sound
- [rules/calculate-metadata.md](rules/calculate-metadata.md) - Dynamically set composition duration, dimensions, props
- [rules/can-decode.md](rules/can-decode.md) - Check if a video can be decoded
- [rules/charts.md](rules/charts.md) - Chart and data visualization patterns
- [rules/compositions.md](rules/compositions.md) - Defining compositions, stills, folders
- [rules/extract-frames.md](rules/extract-frames.md) - Extract frames at specific timestamps
- [rules/fonts.md](rules/fonts.md) - Loading Google Fonts and local fonts
- [rules/get-audio-duration.md](rules/get-audio-duration.md) - Get audio duration
- [rules/get-video-dimensions.md](rules/get-video-dimensions.md) - Get video dimensions
- [rules/get-video-duration.md](rules/get-video-duration.md) - Get video duration
- [rules/gifs.md](rules/gifs.md) - Displaying GIFs synchronized with timeline
- [rules/images.md](rules/images.md) - Embedding images using Img component
- [rules/light-leaks.md](rules/light-leaks.md) - Light leak overlay effects
- [rules/lottie.md](rules/lottie.md) - Embedding Lottie animations
- [rules/maps.md](rules/maps.md) - Add maps using Mapbox
- [rules/measuring-dom-nodes.md](rules/measuring-dom-nodes.md) - Measure DOM element dimensions
- [rules/measuring-text.md](rules/measuring-text.md) - Measure text dimensions, fitting text
- [rules/parameters.md](rules/parameters.md) - Make videos parametrizable with Zod schema
- [rules/sequencing.md](rules/sequencing.md) - Sequencing patterns
- [rules/subtitles.md](rules/subtitles.md) - Captions and subtitles
- [rules/tailwind.md](rules/tailwind.md) - Using TailwindCSS
- [rules/text-animations.md](rules/text-animations.md) - Typography and text animations
- [rules/timing.md](rules/timing.md) - Interpolation curves
- [rules/transitions.md](rules/transitions.md) - Scene transition patterns
- [rules/transparent-videos.md](rules/transparent-videos.md) - Rendering with transparency
- [rules/trimming.md](rules/trimming.md) - Trimming patterns
- [rules/videos.md](rules/videos.md) - Embedding videos

## Best Practices

1. **Use TypeScript** - Remotion has excellent TypeScript support
2. **Leverage React patterns** - Components, hooks, and composition work as expected
3. **Think in frames** - Animation timing is frame-based (30fps = 30 frames per second)
4. **Use the Remotion Preview** - Test compositions before rendering
5. **Consider the render target** - Different targets have different codec support

## Resources

- [Remotion Documentation](https://www.remotion.dev/)
- [Remotion GitHub](https://github.com/remotion-dev/remotion)
- [Remotion Skills Repository](https://github.com/remotion-dev/skills)
