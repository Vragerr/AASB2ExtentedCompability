# AASB2 Extended Compatibility

Modular compatibility fixes and vertical gameplay extensions for As Above, So Below 2 and RimWorld 1.7.

## Stable scenarios verified in the previous release

- Glass floors show the terrain and objects below, including dynamic objects and colored lighting.
- Light transferred through glass is available to gameplay checks. Daylight and plant growth have been tested on a colony map.
- Colonists can use Skipdoors for ordinary movement and work orders across floors.

## Experimental in 0.14.0

- One-hop Skipdoor routing for non-suspendable hauling jobs through Skiptech's native path splicer. The integration consults Skiptech's animal access rules. Mounted access checks are conditional on Giddy-up.
- A vertical ventilation grate exchanges room heat through constructed roofs between adjacent above-ground floors. Open or close it like a standard vent.
- Vertical riser protection reserves pipe cells for the riser's network controls.
- Endless Horizon shares the surface landscape between above-ground floors. The updated surface pass uses the unclipped view and keeps the original cutoffs if the horizon cannot draw.
- A setting limits natural anima tree spawns to 1–50 per map. A separate option keeps natural spawning on the main floor. These options require Royalty.
- Additional optional support includes Dubs Skylights and Factory vertical cargo ports. A selectable temporary safeguard skips pawn silhouettes whose graphics fail to initialize.

## Known limitations

0.14.0 gameplay verification is pending. Non-suspendable hauling across multiple consecutive Skipdoors, mounted stairs, terrain rendering in mountains, and Horizon lighting and seams need further work or verification. See the [release notes](RELEASE_NOTES_0.14.0.md).

## Dependencies and installation

Requires RimWorld 1.7, [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077) and [As Above, So Below 2](https://steamcommunity.com/sharedfiles/filedetails/?id=3776015553). Optional integrations: [MigCorp - Skiptech](https://steamcommunity.com/sharedfiles/filedetails/?id=3626074566), [Endless Horizon](https://steamcommunity.com/sharedfiles/filedetails/?id=3842486043), [Giddy-Up 2 - Continued](https://steamcommunity.com/sharedfiles/filedetails/?id=3674332861), [Dubs Skylights](https://steamcommunity.com/sharedfiles/filedetails/?id=833899765), [Vanilla Furniture Expanded - Factory](https://steamcommunity.com/sharedfiles/filedetails/?id=3686924415).

Extract the ZIP into RimWorld's `Mods` folder. Keep the archive's folder name `mod` so the game can find existing local settings. Enable it after Harmony, AASB2 and relevant optional mods. Avoid installing a second copy with the same package ID.

Options are in Mod Settings. The glass view and gameplay lighting are independent. Horizon changes require a restart. Disabled integrations retain their definitions and existing buildings. Back up your save before removing the mod or changing dependencies.

## Diagnostics and development

Use Mod Settings → AASB2 Extended Compatibility → Diagnostics. Reports are written under `Diagnostics/AASB2Extended/` in the RimWorld user data folder. In a bug report, include the floors, related mods, versions, steps and relevant report excerpts. Review them for personal data first.

[Build and tests](BUILD.ru.md) · [Contributing](CONTRIBUTING.md) · [Changelog](CHANGELOG.md)

Developed by **Vragerr**. Licensing is pending the author's decision. [Third-party notices](THIRD_PARTY_NOTICES.md).
