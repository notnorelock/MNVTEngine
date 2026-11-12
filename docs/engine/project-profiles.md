# Project profiles

Every project is either **2D** or **3D**. The profile is the project's primary rendering mode. It decides which engine modules the project gets, which editor tools open, and which runtime modules an export ships. The two profiles are mutually exclusive. There is no hybrid profile, and a project never becomes one through its content.

> Status: the profile is stored, validated, migrated and chosen at creation (phase 1), and the editor's Scene view follows it. Module manifests, profile-specific runtime start-up and build-time stripping of the other profile's assemblies follow in the next phases; until then, exports still contain every engine module.

## In the project file

```json
{
  "schemaVersion": 2,
  "name": "MyGame",
  "profile": "3d",
  "startupScene": "Scenes/Main.mnvt"
}
```

- `profile` is `"2d"` or `"3d"`, in any case, and is required from schema version 2. Any other value, a number, or a missing profile in a version 2 file fails to load with a `SerializationException` that names `profile`.
- In code it is `GameProject.Profile` (`MNVTEngine.Modules.ProjectProfile.TwoD` / `ThreeD`). `MNVTEngine.Modules` has no engine dependencies, so tooling, runtime and editor all read the same definition.

## Creating a project

```bat
scripts\mnvt.cmd new MyGame --profile 2d            :: Empty 2D Project (empty-2d)
scripts\mnvt.cmd new MyGame --profile 3d            :: Empty 3D Project (empty-3d)
scripts\mnvt.cmd new MyGame --template basic-2d     :: the template's profile
```

`mnvt new` needs a profile: `--profile`, or a `--template` (each template declares one). A template of the other profile is refused (`--profile 3d --template basic-2d`). `mnvt new --list` shows each template's profile.

In the editor's **Project Hub**, New Project asks for 2D or 3D first, then offers that profile's templates, starting with its empty one.

| Template | Profile | Contents |
|---|---|---|
| `empty-2d` | 2D | A camera in an empty scene. |
| `basic-2d` | 2D | A player moved by a C# component, a goal, and a `.mnvts` mission. |
| `empty-3d` | 3D | A perspective camera, a directional light and ambient light. |

A template is a folder in `templates/` with a `template.json`: `id`, `name`, `description` and `profile`. Its `.mnvtproj` must declare the same profile; `ProjectTemplates.Create` checks this.

## Projects from before profiles

Schema version 1 files have no profile. They still load, with `GameProject.NeedsProfileMigration` set, and `ProjectProfileMigration` gives them one:

- **Inference.** The components in every scene (`.mnvt`) and prefab (`.mnvtprefab`) under `Assets` are compared against the built-in components of each profile. Only 2D components (sprites, tilemaps, 2D lights, 2D physics) mean 2D. Only 3D components (meshes, 3D lights, environment, model animation, terrain, 3D physics) mean 3D. Shared components (camera, transform, audio, UI, missions) and game components count for neither. A project with no profile-specific content is 2D, the engine's only mode before profiles; the message says so.
- **Conflicts.** 2D and 3D content together is a conflict. Nothing is chosen automatically: the user must choose.
- **Saving.** `Migrate` writes `"profile"` and `"schemaVersion": 2` and leaves every other setting as it was. The old file is first copied to `<name>.mnvtproj.v1.bak`; an existing backup is never overwritten. The new file is written next to the old one and then swapped in, so a failed write leaves the original intact.

Where it happens:

| Tool | Unambiguous project | Mixed 2D and 3D content |
|---|---|---|
| Editor (open from the hub or the command line) | Saves the inferred profile with a backup, and logs why | The Project Hub explains the conflict, lists examples, and asks **Make it 2D** / **Make it 3D** |
| `mnvt migrate <project> [--profile 2d\|3d]` | Saves the inferred profile (or the given one) | Fails without `--profile`; with it, saves that profile |
| `mnvt build`, `mnvt validate` | Builds as the inferred profile with warning `MNVB110`; the project file is not changed | Error `MNVB109` |
| `mnvt run`, Player | Runs as the inferred profile and logs a warning | Does not start (exit code 2) |

A chosen profile must agree with the content: `--profile 3d` for a project with only 2D content is refused. Only a project with no profile-specific content, or a conflict, accepts either profile. Components of the other profile left in a conflicting project's scenes are reported by later validation.

Exported games always carry their profile. The packaged project file is written at the current schema version with the resolved profile, inferred if necessary.

## Editor

The Scene view follows the profile: 3D projects edit in the 3D view (orbit camera, transform gizmos, terrain tools), and 2D projects in the 2D view. The old 2D/3D toggle and the guess from scene contents are gone.
