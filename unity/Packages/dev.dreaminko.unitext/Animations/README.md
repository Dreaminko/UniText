# UniText Animations & Expressions Menu (Generated Non-Destructively)

UniText generates its Direct BlendTree FX `AnimatorController`, linear 2-keyframe `AnimationClip`s, and `VRCExpressionsMenu` submenu **in memory at avatar build / Play Mode time** via `UniTextNdmfPlugin` (`BuildPhase.Generating`, running before Modular Avatar), saving them directly into the NDMF `AssetSaver` container.

This keeps your project and package free of static FX Controller asset duplication while keeping every `UniTextProp` prefab 100% non-destructive.
