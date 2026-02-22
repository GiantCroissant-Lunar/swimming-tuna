---
name: compositions
description: Defining compositions, stills, folders, default props and dynamic metadata
metadata:
  tags: composition, still, folder, props, metadata
---

A `<Composition>` defines the component, width, height, fps and duration of a renderable video.

It normally is placed in the `src/Root.tsx` file.

```tsx
import { Composition } from "remotion";
import { MyComposition } from "./MyComposition";

export const RemotionRoot = () => {
  return (
    <Composition
      id="MyVideo"
      component={MyComposition}
      durationInFrames={150}
      fps={30}
      width={1920}
      height={1080}
    />
  );
};
```

## Default Props

Pass `defaultProps` to provide initial values for your component.
Values must be JSON-serializable (`Date`, `Map`, `Set`, and `staticFile()` are supported).

```tsx
import { Composition } from "remotion";
import { MyComposition, MyCompositionProps } from "./MyComposition";

export const RemotionRoot = () => {
  return (
    <Composition
      id="MyVideo"
      component={MyComposition}
      durationInFrames={150}
      fps={30}
      width={1920}
      height={1080}
      defaultProps={{
        title: "Hello World",
        color: "#ff0000",
      }}
    />
  );
};
```

Use `type` declarations for props rather than `interface` to ensure `defaultProps` type safety.

## Folders

Use `<Folder>` to organize compositions in the sidebar.
Folder names can only contain letters, numbers, and hyphens.

```tsx
import { Composition, Folder } from "remotion";

export const RemotionRoot = () => {
  return (
    <>
      <Folder name="social-media">
        <Composition
          id="Instagram"
          component={InstagramVideo}
          width={1080}
          height={1080}
          fps={30}
          durationInFrames={150}
        />
      </Folder>
    </>
  );
};
```

## Stills

Use `<Still>` for single-frame images. It does not require `durationInFrames` or `fps`.

```tsx
import { Still } from "remotion";
import { Thumbnail } from "./Thumbnail";

export const RemotionRoot = () => {
  return (
    <Still
      id="Thumbnail"
      component={Thumbnail}
      width={1920}
      height={1080}
    />
  );
};
```

## Calculate Metadata

Use `calculateMetadata` to make dimensions, duration, or props dynamic based on data.

```tsx
import { Composition, CalculateMetadataFunction } from "remotion";
import { MyComposition, MyCompositionProps } from "./MyComposition";

const calculateMetadata: CalculateMetadataFunction<
  MyCompositionProps
> = async ({ props, abortSignal }) => {
  const data = await fetch(`https://api.example.com/video/${props.videoId}`, {
    signal: abortSignal,
  }).then((res) => res.json());

  return {
    durationInFrames: Math.ceil(data.duration * 30),
    props: {
      ...props,
      videoUrl: data.url,
    },
  };
};

export const RemotionRoot = () => {
  return (
    <Composition
      id="MyVideo"
      component={MyComposition}
      width={1920}
      height={1080}
      fps={30}
      calculateMetadata={calculateMetadata}
      defaultProps={{
        videoId: "abc123",
      }}
    />
  );
};
```

The function can return `props`, `durationInFrames`, `width`, `height`, `fps`, and codec-related defaults. It runs once before rendering begins.

## Nesting compositions within another

To add a composition within another composition, you can use the `<Sequence>` component with a `width` and `height` prop to specify the size of the composition.

```tsx
<AbsoluteFill>
  <Sequence width={COMPOSITION_WIDTH} height={COMPOSITION_HEIGHT}>
    <CompositionComponent />
  </Sequence>
</AbsoluteFill>
```
