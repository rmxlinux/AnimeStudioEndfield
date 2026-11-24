# Endfield ACL decoder

`EndfieldACL.cpp` decodes Endfield's ACL 2.1 `compressed_tracks` buffers. The x64 DLL is built against [Animation Compression Library](https://github.com/nfrechette/acl) commit `3ee568542eca4428e1041908b4a644b98e7885bd` and its RTM submodule. The upstream ACL and RTM notices are in `ACL-LICENSE.txt`.

On Windows with MinGW-w64 installed, run `build-native.ps1`. Pass `-Compiler` to use another `g++.exe`, or `-AclSource` to use a checkout at the pinned commit. The script writes `AnimeStudio.Libraries/x64/endfieldacl.dll`; the GUI and CLI projects copy it to their output directories.

The ModelConverter maps ACL transform tracks through `TransformSubTrackMasks`, then matches position, rotation and scale bindings by their order within each mask plane. Unsupported or inconsistent layouts fail with an error rather than producing incorrectly bound motion. The native DLL is x64-only.

To validate a real animation bundle, run `dotnet run --project native/EndfieldACL/SmokeTest -c Release -- <bundle.ab> <output.fbx>`. This uses a generated test skeleton to exercise the decoder, ModelConverter binding, and FBX exporter. It does not replace a model-specific export test. Run `build-endfield-acl.ps1` to create separate net8 and net9 packages without replacing the original `dist` directory.
