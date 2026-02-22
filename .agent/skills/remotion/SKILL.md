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

- [rules/animations.md](rules/animations.md) - Fundamental animation skills
- [rules/compositions.md](rules/compositions.md) - Defining compositions, stills, folders
- [rules/sequencing.md](rules/sequencing.md) - Sequencing patterns
- [rules/timing.md](rules/timing.md) - Interpolation curves

Additional rules are available in the [Remotion Skills Repository](https://github.com/remotion-dev/skills/tree/main/skills/remotion/rules).

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
