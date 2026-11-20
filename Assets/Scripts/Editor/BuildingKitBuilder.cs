using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Generates the parkour building prefabs from the specs in <see cref="Specs"/>, plus a test scene to run them in.
///
/// Each building is its own prefab with its own parts (a roof body shaped to the footprint, plus blocks on the roof),
/// built exactly to its spec. The reuse is in this code: every building is made the same way, so changing how a kind
/// of part is built changes it on every building at the next rebuild.
///
/// The specs are the source of truth: rebuilding overwrites the prefabs, so hand edits to them are lost. Change the
/// numbers here instead, or stop rebuilding a building once it's final and edit it by hand from then on.
///
/// Coordinates (metres): x = west -> east, z = south -> north, y = up. A building's pivot is the south-west corner of
/// its footprint's bounding box, at ground level.
///
/// Menu: Tools > Kit > Build Buildings (prefabs only) / Build Test Scene (prefabs + Assets/Scenes/KitTest.unity).
/// Command line: -executeMethod BuildingKitBuilder.BuildFromCommandLine [-kitShots &lt;folder&gt;] builds both and, with
/// -kitShots, saves screenshots of the test scene to that folder.
/// </summary>
public static class BuildingKitBuilder
{
    const string BuildingFolder = "Assets/Kit/Buildings";
    const string MeshFolder = "Assets/Kit/Buildings/Meshes";
    const string MaterialFolder = "Assets/Kit/Materials";
    const string TestScenePath = "Assets/Scenes/KitTest.unity";
    const string PlayerSourceScenePath = "Assets/Scenes/SampleScene.unity";
    const string PlayerName = "Player";

    // Player size and reach, from the movement scripts' defaults (FirstPersonController, FirstPersonMantle,
    // FirstPersonCrouchSlide, CharacterController). If the player changes, update these: the build then warns about
    // parts that no longer work.
    const float PlayerRadius = 0.5f;       // capsule radius: the player is 1 m wide
    const float JumpHeight = 1.2f;         // anything lower can be hopped over
    const float CrouchHeight = 1.1f;       // crouch and slide fit under anything higher (beams use 1.25 m)
    const float MantleReach = 3.4f;        // jump 1.2 m + mantle 2.3 m above the feet, a bit under
    const float MinMantleDepth = 0.7f;     // the mantle looks for standing room 0.6 m past the wall face (radius + 0.1)

    // ------------------------------------------------------------------ specs

    /// <summary>
    /// A solid box on the roof, from (X0, Z0) to (X1, Z1), Height tall, its bottom BaseY above the roof: rooms, walls,
    /// fences, beams, props. Decor parts (posts, masts) aren't meant to be climbed, so the build doesn't check them.
    /// </summary>
    sealed class Block
    {
        public string Name, Material;
        public float X0, Z0, X1, Z1, Height, BaseY;
        public bool Decor;
    }

    /// <summary>
    /// A plank or ramp, its top surface running from From to To: between buildings in world space (<see cref="Planks"/>),
    /// or inside one building relative to its pivot (<see cref="BuildingSpec.Planks"/>). Narrow planks (pipes, beams
    /// to balance on) are meant to be thinner than the player, so the width check skips them.
    /// </summary>
    sealed class Plank
    {
        public string Name;
        public Vector3 From, To;
        public float Width = 1f, Thickness = 0.15f;
        public bool Narrow;
    }

    /// <summary>A free-standing box in the test layout (world space), e.g. a billboard to wall-run along.</summary>
    sealed class Structure
    {
        public string Name, Material;
        public Vector3 Min, Max;
    }

    /// <summary>A checkpoint in the test layout: where the player's feet respawn, facing Yaw, and the trigger size.</summary>
    sealed class CheckpointSpec
    {
        public string Name;
        public Vector3 Feet, Size;
        public float Yaw;
    }

    /// <summary>
    /// A scaffold deck from (x0, z0) to (x1, z1) whose top is topAboveRoof above the building's roof (negative = below
    /// it, e.g. on the side of the building), standing on four poles that reach down poleLength metres from the top.
    /// Thin decks can't be mantled from below; add a <see cref="Ledger"/> under the edge you climb onto, and don't put
    /// another deck right above the spot you climb from (the mantle needs head room to pull up).
    /// </summary>
    static IEnumerable<Block> Scaffold(string name, float x0, float z0, float x1, float z1, float topAboveRoof, float poleLength)
    {
        const float deck = 0.2f, pole = 0.12f;
        yield return new Block { Name = name, Material = "KIT_Scaffold", X0 = x0, Z0 = z0, X1 = x1, Z1 = z1, Height = deck, BaseY = topAboveRoof - deck };
        int i = 0;
        foreach (float x in new[] { x0, x1 - pole })
            foreach (float z in new[] { z0, z1 - pole })
                yield return new Block
                {
                    Name = $"{name}_Pole{++i}", Material = "KIT_Post", X0 = x, Z0 = z, X1 = x + pole, Z1 = z + pole,
                    Height = poleLength - deck, BaseY = topAboveRoof - poleLength, Decor = true,
                };
    }

    /// <summary>
    /// A ledger: the horizontal scaffold bar just under a deck's edge, 0.5 m deep. It is the face the mantle grabs to
    /// climb onto a deck whose top is 1.8 to 2.5 m above where you stand (a bare 0.2 m deck edge is too thin to catch).
    /// Thin, so it is Decor: the deck above is what you land on.
    /// </summary>
    static Block Ledger(string name, float x0, float z0, float x1, float z1, float deckTopAboveRoof) =>
        new Block
        {
            Name = name, Material = "KIT_Post", X0 = x0, Z0 = z0, X1 = x1, Z1 = z1,
            BaseY = deckTopAboveRoof - 0.7f, Height = 0.5f, Decor = true,
        };

    /// <summary>Lets a building's Blocks list take a whole <see cref="Scaffold"/> in its initializer.</summary>
    static void Add(this List<Block> list, IEnumerable<Block> items) => list.AddRange(items);

    /// <summary>
    /// A flat slab with its own outline, e.g. a ledge sticking out from the walls with nothing underneath. Its top is
    /// TopY above the ground. Overhang is how far it sticks out past the walls, checked against the player's width.
    /// </summary>
    sealed class Slab
    {
        public string Name, Material;
        public Vector2[] Outline;       // convex, in (x, z)
        public float TopY, Thickness, Overhang;
    }

    sealed class BuildingSpec
    {
        public string Name;
        public Vector2[] Footprint;     // the building's walls, in (x, z), convex, either winding
        public float Height;            // roof height above the ground
        public List<Block> Blocks = new List<Block>();
        public List<Slab> Slabs = new List<Slab>();
        public List<Plank> Planks = new List<Plank>();   // ramps and planks inside this building, relative to its pivot
        public Vector3 TestPosition;    // where the test scene places the building (its pivot)
    }

    // The test scene's player start, in world space: BLD_01's west section, facing east.
    static readonly Vector3 TestSpawn = new Vector3(5f, 12f, 13f);
    const float TestSpawnYaw = 90f;     // 0 = facing north, 90 = facing east

    static List<BuildingSpec> Specs() => new List<BuildingSpec>
    {
        // BLD_01: storage roof. Go round the wall's south end or mantle over it (or run along its top and hop onto
        // the storage room), mantle onto the storage room, and jump off its north side to the next building.
        new BuildingSpec
        {
            Name = "BLD_01_StorageRoof",
            Footprint = new[]
            {
                new Vector2(0f, 5f), new Vector2(11f, 0f), new Vector2(22f, 0f),   // cut south-west corner
                new Vector2(22f, 20f), new Vector2(0f, 20f),
            },
            Height = 12f,
            Blocks =
            {
                // 1 m thick so it can be mantled and run along (0.5 m is too thin for the mantle to land on).
                new Block { Name = "Wall", Material = "KIT_Wall", X0 = 11f, Z0 = 6f, X1 = 12f, Z1 = 20f, Height = 2.5f },
                // 2 m from the wall: a hop from the wall top (2.5 m) up onto the storage roof (3 m).
                new Block { Name = "StorageRoom", Material = "KIT_Storage", X0 = 14f, Z0 = 12f, X1 = 22f, Z1 = 20f, Height = 3f },
            },
            TestPosition = Vector3.zero,
        },

        // BLD_02: narrow landing building north of BLD_01's storage room. A 6 x 10 m building (townhouse-like: narrow
        // towards BLD_01, long away from it) with a 1 m ledge sticking out from its walls all round, 1 m below the
        // roof, with nothing underneath. Land on the roof, or fall short onto the ledge (or grab it) and hop up; miss
        // both and you fall. A small utility box in the middle of the roof to hop over.
        new BuildingSpec
        {
            Name = "BLD_02_LandingRoof",
            Footprint = new[]
            {
                new Vector2(1f, 1f), new Vector2(7f, 1f), new Vector2(7f, 11f), new Vector2(1f, 11f),
            },
            Height = 11.5f,   // the roof; the ledge is 1 m lower, 4.5 m below the storage roof's launch edge (15 m)
            Slabs =
            {
                new Slab
                {
                    Name = "Ledge", Material = "KIT_Ledge", TopY = 10.5f, Thickness = 0.5f, Overhang = 1f,
                    Outline = new[]
                    {
                        new Vector2(0f, 0f), new Vector2(8f, 0f), new Vector2(8f, 12f),
                        new Vector2(1f, 12f), new Vector2(0f, 11f),                   // cut north-west corner
                    },
                },
            },
            Blocks =
            {
                // Small utility box in the middle of the roof (later: the player reaches this roof by ladder).
                // Low enough to hop over.
                new Block { Name = "UtilityBox", Material = "KIT_Utility", X0 = 3.5f, Z0 = 5.5f, X1 = 4.5f, Z1 = 6.5f, Height = 1f },
            },
            // Centred on the storage room's launch edge (x 14 -> 22), 4 m north of BLD_01 (z 20 + 4).
            TestPosition = new Vector3(14f, 0f, 24f),
        },

        // ---- Split: from BLD_02 the route splits and rejoins at BLD_04. ----
        // Hard and early: right after landing on BLD_02, turn east and make a long jump down to BLD_03A, then hop up
        // to BLD_04's south end and slide under its beam.
        // Easy and late: run to BLD_02's north end, hop to BLD_03B, cross the plank (or jump and mantle) up to
        // BLD_03C, and drop to BLD_04's north end, then hop its fence.

        // BLD_03A: hard route. 5.5 m from BLD_02's roof edge, 1.5 m lower. An AC unit to hop on the way across.
        new BuildingSpec
        {
            Name = "BLD_03A_DropRoof",
            Footprint = Rect(8f, 7f),
            Height = 10f,
            Blocks =
            {
                new Block { Name = "ACUnit", Material = "KIT_Prop", X0 = 3.5f, Z0 = 1f, X1 = 5f, Z1 = 2.5f, Height = 1f },
            },
            TestPosition = new Vector3(26.5f, 0f, 26f),   // x 26.5 -> 34.5, z 26 -> 33
        },

        // BLD_03B: easy route. 2.5 m north of BLD_02's ledge. A 1 m fence across the way to the plank: hop it, or go
        // round its north end.
        new BuildingSpec
        {
            Name = "BLD_03B_FenceRoof",
            Footprint = Rect(10f, 8f),
            Height = 11f,
            Blocks =
            {
                new Block { Name = "Fence", Material = "KIT_Fence", X0 = 6f, Z0 = 0.5f, X1 = 6.1f, Z1 = 6.5f, Height = 1f },
            },
            TestPosition = new Vector3(13f, 0f, 38.5f),   // x 13 -> 23, z 38.5 -> 46.5
        },

        // BLD_03C: easy route. 3 m east of BLD_03B and 1.5 m higher: take the plank, or jump and mantle.
        new BuildingSpec
        {
            Name = "BLD_03C_HighRoof",
            Footprint = Rect(8f, 8f),
            Height = 12.5f,
            Blocks =
            {
                new Block { Name = "Vent", Material = "KIT_Prop", X0 = 5f, Z0 = 5.5f, X1 = 6f, Z1 = 6.5f, Height = 0.8f },
            },
            TestPosition = new Vector3(26f, 0f, 38f),   // x 26 -> 34, z 38 -> 46
        },

        // BLD_04: where the routes rejoin. A long apartment block east of both: the hard route lands at its south
        // end and must slide under a beam, the easy route lands at its north end and hops a fence (or goes round its
        // east end). Both head for the plank on the east side, halfway along.
        new BuildingSpec
        {
            Name = "BLD_04_ApartmentBlock",
            Footprint = Rect(10f, 22f),
            Height = 11f,
            Blocks =
            {
                new Block { Name = "LowBeam", Material = "KIT_Beam", X0 = 0.5f, Z0 = 7.8f, X1 = 9.5f, Z1 = 8.2f, Height = 0.3f, BaseY = 1.25f },
                new Block { Name = "BeamPost_W", Material = "KIT_Post", X0 = 0.5f, Z0 = 7.85f, X1 = 0.8f, Z1 = 8.15f, Height = 1.55f, Decor = true },
                new Block { Name = "BeamPost_E", Material = "KIT_Post", X0 = 9.2f, Z0 = 7.85f, X1 = 9.5f, Z1 = 8.15f, Height = 1.55f, Decor = true },
                new Block { Name = "Fence", Material = "KIT_Fence", X0 = 0f, Z0 = 13f, X1 = 7.5f, Z1 = 13.1f, Height = 1f },
                new Block { Name = "WaterTank", Material = "KIT_Prop", X0 = 7f, Z0 = 17f, X1 = 9f, Z1 = 19f, Height = 2.5f },
            },
            TestPosition = new Vector3(37f, 0f, 25f),   // x 37 -> 47, z 25 -> 47
        },

        // BLD_05: across a 7 m street from BLD_04, 2 m lower. Cross on the plank (safe), or make the long drop-jump
        // (risky, faster). A water-tank stand at the north end to mantle up and launch from.
        new BuildingSpec
        {
            Name = "BLD_05_StreetSide",
            Footprint = Rect(10f, 12f),
            Height = 9f,
            Blocks =
            {
                new Block { Name = "TankStand", Material = "KIT_Storage", X0 = 3f, Z0 = 9f, X1 = 7f, Z1 = 12f, Height = 2.5f },
                new Block { Name = "Vent", Material = "KIT_Prop", X0 = 6.5f, Z0 = 3f, X1 = 7.5f, Z1 = 4f, Height = 0.8f },
            },
            TestPosition = new Vector3(54f, 0f, 28f),   // x 54 -> 64, z 28 -> 40
        },

        // BLD_06: end of this section. 4 m north of BLD_05's tank stand and 0.5 m above its top; a strong jump from
        // BLD_05's roof can also catch its edge with a mantle. The antenna mast is a landmark you can see from the
        // split.
        new BuildingSpec
        {
            Name = "BLD_06_MastRoof",
            Footprint = Rect(10f, 12f),
            Height = 12f,
            Blocks =
            {
                new Block { Name = "ACUnit_1", Material = "KIT_Prop", X0 = 2f, Z0 = 4f, X1 = 3.5f, Z1 = 5f, Height = 1f },
                new Block { Name = "ACUnit_2", Material = "KIT_Prop", X0 = 5f, Z0 = 6.5f, X1 = 6.5f, Z1 = 7.5f, Height = 1f },
                new Block { Name = "Mast", Material = "KIT_Post", X0 = 8f, Z0 = 9f, X1 = 8.4f, Z1 = 9.4f, Height = 8f, Decor = true },
            },
            TestPosition = new Vector3(56f, 0f, 44f),   // x 56 -> 66, z 44 -> 56
        },

        // ================================================================== section 2: harder, multi-level
        // Route: up BLD_07's scaffolding, across its roof (slide, fence), wall-run along a billboard to BLD_08, split
        // (BLD_09A hard / BLD_09B easy), up BLD_10's terraces through a zigzag, across BLD_11's scaffold bridge,
        // wall-run (or balance on a pipe) to BLD_12, slide down its ramp, jump to BLD_13's ledge and edge round it,
        // then split again (wall-run straight to BLD_15, or drop to BLD_14 and climb its scaffold).
        // Movement numbers this is sized from: sprint 11 m/s, jump 1.2 m, falls at 2x gravity (flat jump limit about
        // 7 m), mantle up to about 3.4 m, crouch 1.1 m, wall-run from 6 m/s within about 1.1 m of the wall.

        // BLD_07: building under construction, scaffolding up its south face. Open decks on poles, stepping up to the
        // east like stairs: jump 4 m up 1 m from BLD_06 onto the first deck, jump and catch the bar under the next
        // deck's edge to mantle up (twice), then mantle onto the roof. On the roof: slide under the beam, hop the
        // fence (or go round its west end), and run west along the north edge into the wall-run.
        new BuildingSpec
        {
            Name = "BLD_07_ScaffoldTower",
            Footprint = new[] { new Vector2(0f, 3f), new Vector2(12f, 3f), new Vector2(12f, 17f), new Vector2(0f, 17f) },
            Height = 20f,
            Blocks =
            {
                Scaffold("Deck_1", 1f, 0f, 5f, 3f, -7f, 13f),        // top 13
                Scaffold("Deck_2", 5f, 0f, 9f, 3f, -4.5f, 15.5f),    // top 15.5
                Scaffold("Deck_3", 9f, 0f, 12f, 3f, -2f, 18f),       // top 18
                Ledger("Ledger_2", 5f, 0f, 5.1f, 3f, -4.5f),         // under Deck_2's west edge: grab it from Deck_1
                Ledger("Ledger_3", 9f, 0f, 9.1f, 3f, -2f),           // under Deck_3's west edge: grab it from Deck_2
                new Block { Name = "Column_1", Material = "KIT_Post", X0 = 2f, Z0 = 6f, X1 = 2.4f, Z1 = 6.4f, Height = 2.5f, Decor = true },
                new Block { Name = "Column_2", Material = "KIT_Post", X0 = 10f, Z0 = 6f, X1 = 10.4f, Z1 = 6.4f, Height = 2.5f, Decor = true },
                new Block { Name = "Column_3", Material = "KIT_Post", X0 = 2f, Z0 = 12.5f, X1 = 2.4f, Z1 = 12.9f, Height = 2.5f, Decor = true },
                new Block { Name = "Column_4", Material = "KIT_Post", X0 = 10f, Z0 = 12.5f, X1 = 10.4f, Z1 = 12.9f, Height = 2.5f, Decor = true },
                new Block { Name = "LowBeam", Material = "KIT_Beam", X0 = 0.5f, Z0 = 9f, X1 = 11.5f, Z1 = 9.4f, Height = 0.3f, BaseY = 1.25f },
                new Block { Name = "BeamPost_W", Material = "KIT_Post", X0 = 0.5f, Z0 = 9.05f, X1 = 0.8f, Z1 = 9.35f, Height = 1.55f, Decor = true },
                new Block { Name = "BeamPost_E", Material = "KIT_Post", X0 = 11.2f, Z0 = 9.05f, X1 = 11.5f, Z1 = 9.35f, Height = 1.55f, Decor = true },
                new Block { Name = "Fence", Material = "KIT_Fence", X0 = 4f, Z0 = 13.5f, X1 = 12f, Z1 = 13.6f, Height = 1f },
            },
            TestPosition = new Vector3(55f, 0f, 60f),   // scaffold z 60 -> 63, building x 55 -> 67, z 63 -> 77
        },

        // BLD_08: reached by wall-running the billboard across the 7 m gap from BLD_07 (or a very long jump, 1 m
        // down). Then split: north to BLD_09A (hard), or west to BLD_09B (easy).
        new BuildingSpec
        {
            Name = "BLD_08_SplitRoof",
            Footprint = Rect(10f, 12f),
            Height = 19f,
            Blocks =
            {
                new Block { Name = "ACUnit", Material = "KIT_Prop", X0 = 6f, Z0 = 8f, X1 = 7.5f, Z1 = 9.5f, Height = 1f },
                new Block { Name = "Vent", Material = "KIT_Prop", X0 = 2f, Z0 = 3f, X1 = 3f, Z1 = 4f, Height = 0.8f },
            },
            TestPosition = new Vector3(38f, 0f, 64f),   // x 38 -> 48, z 64 -> 76
        },

        // BLD_09A: hard route. 5 m north of BLD_08, 0.5 m lower, only 6 m wide. Slide under the beam, hop the fence
        // 1.5 m before the edge and jump straight on to BLD_10.
        new BuildingSpec
        {
            Name = "BLD_09A_NarrowRun",
            Footprint = Rect(6f, 12f),
            Height = 18.5f,
            Blocks =
            {
                new Block { Name = "LowBeam", Material = "KIT_Beam", X0 = 0.3f, Z0 = 5f, X1 = 5.7f, Z1 = 5.4f, Height = 0.3f, BaseY = 1.25f },
                new Block { Name = "BeamPost_W", Material = "KIT_Post", X0 = 0.3f, Z0 = 5.05f, X1 = 0.6f, Z1 = 5.35f, Height = 1.55f, Decor = true },
                new Block { Name = "BeamPost_E", Material = "KIT_Post", X0 = 5.4f, Z0 = 5.05f, X1 = 5.7f, Z1 = 5.35f, Height = 1.55f, Decor = true },
                new Block { Name = "Fence", Material = "KIT_Fence", X0 = 0f, Z0 = 10.5f, X1 = 6f, Z1 = 10.6f, Height = 1f },
            },
            TestPosition = new Vector3(40f, 0f, 81f),   // x 40 -> 46, z 81 -> 93
        },

        // BLD_09B: easy route. 3 m west of BLD_08, 2 m lower. Run north, mantle up the 2.5 m step to the upper
        // level, and jump 4 m on to BLD_10 (1 m down). Longer, with a mantle, but no tricky jumps.
        new BuildingSpec
        {
            Name = "BLD_09B_StepRoof",
            Footprint = Rect(10f, 22f),
            Height = 17f,
            Blocks =
            {
                new Block { Name = "UpperLevel", Material = "KIT_Raised", X0 = 0f, Z0 = 12f, X1 = 10f, Z1 = 22f, Height = 2.5f },
                new Block { Name = "WaterTank", Material = "KIT_Prop", X0 = 1f, Z0 = 16f, X1 = 3f, Z1 = 18f, Height = 2.5f, BaseY = 2.5f },
                new Block { Name = "Vent", Material = "KIT_Prop", X0 = 6f, Z0 = 4f, X1 = 7f, Z1 = 5f, Height = 0.8f },
            },
            TestPosition = new Vector3(25f, 0f, 70f),   // x 25 -> 35, z 70 -> 92
        },

        // BLD_10: where the routes rejoin. A big terraced block. Hop the fence, mantle up to the first terrace, then
        // zigzag between two walls too tall to climb (east, west through a corridor with a beam to slide under), mantle
        // up to the west half of the top terrace, jump the 3 m light well to the east half (fall in and you climb
        // back out), and leave from its north edge.
        new BuildingSpec
        {
            Name = "BLD_10_TerraceBlock",
            Footprint = Rect(26f, 24f),
            Height = 18.5f,
            Blocks =
            {
                new Block { Name = "Fence", Material = "KIT_Fence", X0 = 3f, Z0 = 4f, X1 = 23f, Z1 = 4.1f, Height = 1f },
                new Block { Name = "Terrace_1", Material = "KIT_Raised", X0 = 0f, Z0 = 8f, X1 = 26f, Z1 = 24f, Height = 2.5f },
                new Block { Name = "Wall_1", Material = "KIT_CityBlock", X0 = 0f, Z0 = 10.5f, X1 = 18f, Z1 = 11f, Height = 4f, BaseY = 2.5f },
                new Block { Name = "Wall_2", Material = "KIT_CityBlock", X0 = 8f, Z0 = 13.25f, X1 = 26f, Z1 = 13.75f, Height = 4f, BaseY = 2.5f },
                new Block { Name = "CorridorBeam", Material = "KIT_Beam", X0 = 12f, Z0 = 11f, X1 = 12.4f, Z1 = 13.25f, Height = 0.3f, BaseY = 3.75f },
                new Block { Name = "Terrace_2W", Material = "KIT_Raised", X0 = 0f, Z0 = 16f, X1 = 11f, Z1 = 24f, Height = 2.5f, BaseY = 2.5f },
                new Block { Name = "Terrace_2E", Material = "KIT_Raised", X0 = 14f, Z0 = 16f, X1 = 26f, Z1 = 24f, Height = 2.5f, BaseY = 2.5f },
            },
            TestPosition = new Vector3(24f, 0f, 96f),   // x 24 -> 50, z 96 -> 120
        },

        // BLD_11: a scaffold bridge over the street, round the lift shaft of an unfinished building. Drop 1.5 m onto
        // the first deck, slide under its crossbar, jump two 3 m gaps, then either wall-run the billboard 10 m to
        // BLD_12 or balance along the narrow pipe.
        new BuildingSpec
        {
            Name = "BLD_11_ScaffoldBridge",
            Footprint = new[] { new Vector2(10f, 6f), new Vector2(13f, 6f), new Vector2(13f, 9f), new Vector2(10f, 9f) },   // lift shaft
            Height = 28f,
            Blocks =
            {
                Scaffold("Deck_1", 0f, 1f, 10f, 4f, -6f, 22f),       // top 22
                Scaffold("Deck_2", 13f, 1f, 19f, 4f, -5.5f, 22.5f),  // top 22.5
                Scaffold("Deck_3", 22f, 0f, 28f, 5f, -7f, 21f),      // top 21
                new Block { Name = "Crossbar", Material = "KIT_Beam", X0 = 6f, Z0 = 1f, X1 = 6.3f, Z1 = 4f, Height = 0.3f, BaseY = -4.75f },
                new Block { Name = "CrossbarPost_S", Material = "KIT_Post", X0 = 6f, Z0 = 1f, X1 = 6.3f, Z1 = 1.15f, Height = 1.55f, BaseY = -6f, Decor = true },
                new Block { Name = "CrossbarPost_N", Material = "KIT_Post", X0 = 6f, Z0 = 3.85f, X1 = 6.3f, Z1 = 4f, Height = 1.55f, BaseY = -6f, Decor = true },
            },
            TestPosition = new Vector3(36f, 0f, 122f),   // decks x 36 -> 64, z 122 -> 127; lift shaft x 46 -> 49, z 128 -> 131
        },

        // BLD_12: lands on the penthouse. Slide down the ramp (or drop off the edge) to the roof and jump 4 m, 1 m up,
        // to BLD_13's ledge.
        new BuildingSpec
        {
            Name = "BLD_12_Penthouse",
            Footprint = Rect(12f, 16f),
            Height = 17f,
            Blocks =
            {
                new Block { Name = "Penthouse", Material = "KIT_Raised", X0 = 0f, Z0 = 0f, X1 = 12f, Z1 = 10f, Height = 3.4f },
                new Block { Name = "ACUnit", Material = "KIT_Prop", X0 = 8f, Z0 = 2f, X1 = 9.5f, Z1 = 3f, Height = 1f, BaseY = 3.4f },
            },
            Planks =
            {
                new Plank { Name = "SlideRamp", From = new Vector3(6f, 20.4f, 9.5f), To = new Vector3(6f, 17f, 14f), Width = 2f },
            },
            TestPosition = new Vector3(74f, 0f, 118f),   // x 74 -> 86, z 118 -> 134
        },

        // BLD_13: the roof is 4.5 m above its 1 m ledge, too high to climb. Land on the south ledge, edge east and
        // round the corner, crouch under the pipe, climb the crate and mantle onto the roof. Then split: wall-run the
        // billboard north straight to BLD_15 (hard), or drop east to BLD_14 (easy).
        new BuildingSpec
        {
            Name = "BLD_13_LedgeTower",
            Footprint = new[] { new Vector2(1f, 1f), new Vector2(13f, 1f), new Vector2(13f, 15f), new Vector2(1f, 15f) },
            Height = 22.5f,
            Slabs =
            {
                new Slab { Name = "Ledge", Material = "KIT_Ledge", Outline = Rect(14f, 16f), TopY = 18f, Thickness = 0.5f, Overhang = 1f },
            },
            Blocks =
            {
                new Block { Name = "LedgePipe", Material = "KIT_Beam", X0 = 13f, Z0 = 7f, X1 = 14f, Z1 = 7.3f, Height = 0.3f, BaseY = -3.25f },
                new Block { Name = "Crate", Material = "KIT_Storage", X0 = 13f, Z0 = 11f, X1 = 14f, Z1 = 12.5f, Height = 2.5f, BaseY = -4.5f },
            },
            TestPosition = new Vector3(73f, 0f, 138f),   // ledge x 73 -> 87, z 138 -> 154; building x 74 -> 86, z 139 -> 153
        },

        // BLD_14: easy route. 4 m east of BLD_13's roof, 0.5 m lower (and too high to reach from the ledge). Hop the
        // fence, catch the bar under the scaffold deck and mantle up, and jump 4 m (1.5 m down) to BLD_15.
        new BuildingSpec
        {
            Name = "BLD_14_ScaffoldRoof",
            Footprint = Rect(10f, 15f),
            Height = 22f,
            Blocks =
            {
                new Block { Name = "Fence", Material = "KIT_Fence", X0 = 0f, Z0 = 6f, X1 = 7f, Z1 = 6.1f, Height = 1f },
                Scaffold("Deck", 2f, 12f, 8f, 15f, 2.5f, 2.5f),      // top 24.5
                Ledger("Ledger", 2f, 12f, 8f, 12.1f, 2.5f),           // under the deck's south edge: grab it from the roof
            },
            TestPosition = new Vector3(90f, 0f, 145f),   // x 90 -> 100, z 145 -> 160
        },

        // BLD_15: end of section 2, where both routes rejoin. A crown to mantle up to, with a mast as the landmark.
        new BuildingSpec
        {
            Name = "BLD_15_FinishTower",
            Footprint = Rect(18f, 16f),
            Height = 23f,
            Blocks =
            {
                new Block { Name = "Crown", Material = "KIT_Raised", X0 = 6f, Z0 = 6f, X1 = 12f, Z1 = 12f, Height = 3f },
                new Block { Name = "Mast", Material = "KIT_Post", X0 = 8.8f, Z0 = 8.8f, X1 = 9.2f, Z1 = 9.2f, Height = 10f, BaseY = 3f, Decor = true },
                new Block { Name = "ACUnit", Material = "KIT_Prop", X0 = 2f, Z0 = 9f, X1 = 3.5f, Z1 = 10f, Height = 1f },
            },
            TestPosition = new Vector3(78f, 0f, 164f),   // x 78 -> 96, z 164 -> 180
        },
    };

    // Billboards to wall-run along (world space). Teal = wall-run surface.
    static List<Structure> Structures() => new List<Structure>
    {
        // Along BLD_07's north edge and over the 7 m gap to BLD_08: run west with it on your right.
        new Structure { Name = "Billboard_07_08", Material = "KIT_Billboard", Min = new Vector3(46f, 17f, 76.6f), Max = new Vector3(57f, 25f, 76.9f) },
        // Along BLD_11's last deck and over the 10 m gap to BLD_12: run east with it on your left.
        new Structure { Name = "Billboard_11_12", Material = "KIT_Billboard", Min = new Vector3(60f, 17f, 127.6f), Max = new Vector3(78f, 27f, 127.9f) },
        // Across BLD_13's roof and the 11 m gap to BLD_15: run north with it on your left.
        new Structure { Name = "Billboard_13_15", Material = "KIT_Billboard", Min = new Vector3(79.7f, 19f, 147f), Max = new Vector3(80f, 29f, 167f) },
    };

    // Checkpoints (world space): rest points after each hard stretch.
    static List<CheckpointSpec> Checkpoints() => new List<CheckpointSpec>
    {
        new CheckpointSpec { Name = "CP_BLD_06", Feet = new Vector3(60f, 12f, 46f), Size = new Vector3(8f, 3f, 4f) },
        new CheckpointSpec { Name = "CP_BLD_07", Feet = new Vector3(60f, 20f, 65f), Size = new Vector3(10f, 3f, 4f) },
        new CheckpointSpec { Name = "CP_BLD_10", Feet = new Vector3(44f, 23.5f, 116f), Size = new Vector3(12f, 3f, 8f) },
        new CheckpointSpec { Name = "CP_BLD_12", Feet = new Vector3(78f, 20.4f, 124f), Size = new Vector3(8f, 3f, 8f) },
        new CheckpointSpec { Name = "CP_BLD_15", Feet = new Vector3(87f, 23f, 168f), Size = new Vector3(18f, 3f, 6f) },
    };

    static Vector2[] Rect(float width, float depth) =>
        new[] { new Vector2(0f, 0f), new Vector2(width, 0f), new Vector2(width, depth), new Vector2(0f, depth) };

    // Planks between buildings in the test layout (world space; ends rest 0.5 m onto each roof).
    static List<Plank> Planks() => new List<Plank>
    {
        // BLD_03B -> BLD_03C: up 1.5 m over a 3 m gap.
        new Plank { Name = "Plank_03B_03C", From = new Vector3(22.5f, 11f, 42f), To = new Vector3(26.5f, 12.5f, 42f), Width = 1.2f },
        // BLD_04 -> BLD_05: across the 7 m street, down 2 m.
        new Plank { Name = "Plank_04_05", From = new Vector3(46.5f, 11f, 35.5f), To = new Vector3(54.5f, 9f, 35.5f), Width = 1f },
        // BLD_11 -> BLD_12: a narrow pipe to balance along instead of wall-running.
        new Plank { Name = "Pipe_11_12", From = new Vector3(63.5f, 21f, 123.5f), To = new Vector3(74.5f, 20.4f, 123.5f), Width = 0.6f, Thickness = 0.3f, Narrow = true },
    };

    // ------------------------------------------------------------------ menu / command line

    [MenuItem("Tools/Kit/Build Buildings")]
    static void BuildBuildingsMenu()
    {
        BuildBuildings();
        Debug.Log("Kit: buildings rebuilt in " + BuildingFolder);
    }

    [MenuItem("Tools/Kit/Build Test Scene")]
    static void BuildTestSceneMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        BuildBuildings();
        BuildTestScene();
        Debug.Log("Kit: test scene rebuilt at " + TestScenePath);
    }

    /// <summary>Entry point for Unity's -executeMethod. Exits with 0 on success, 1 on failure.</summary>
    public static void BuildFromCommandLine()
    {
        try
        {
            BuildBuildings();
            BuildTestScene();
            string shots = GetArg("-kitShots");
            if (!string.IsNullOrEmpty(shots)) CaptureShots(shots);
            Debug.Log("Kit: build finished OK");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    static string GetArg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    // ------------------------------------------------------------------ buildings

    static void BuildBuildings()
    {
        EnsureFolder(BuildingFolder);
        EnsureFolder(MeshFolder);
        EnsureFolder(MaterialFolder);

        foreach (BuildingSpec spec in Specs())
        {
            Check(spec);
            BuildBuilding(spec);
        }
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    static void BuildBuilding(BuildingSpec spec)
    {
        var root = new GameObject(spec.Name);
        try
        {
            // The building's mass, shaped to the footprint: roof on top, facade on the sides.
            Mesh mesh = SaveMesh(spec.Name + "_Body", m => BuildPrism(m, spec.Footprint, 0f, spec.Height));
            var body = new GameObject("Body");
            body.transform.SetParent(root.transform, false);
            body.AddComponent<MeshFilter>().sharedMesh = mesh;
            body.AddComponent<MeshRenderer>().sharedMaterials = new[]
            {
                Mat("KIT_Roof", new Color(0.72f, 0.74f, 0.78f)),
                Mat("KIT_Facade", new Color(0.46f, 0.48f, 0.52f)),
            };
            body.AddComponent<MeshCollider>().sharedMesh = mesh;

            foreach (Slab s in spec.Slabs)
            {
                Mesh slabMesh = SaveMesh($"{spec.Name}_{s.Name}", m => BuildPrism(m, s.Outline, s.TopY - s.Thickness, s.TopY));
                var slab = new GameObject(s.Name);
                slab.transform.SetParent(root.transform, false);
                slab.AddComponent<MeshFilter>().sharedMesh = slabMesh;
                Material slabMat = BlockMat(s.Material);
                slab.AddComponent<MeshRenderer>().sharedMaterials = new[] { slabMat, slabMat };
                slab.AddComponent<MeshCollider>().sharedMesh = slabMesh;
            }

            foreach (Block b in spec.Blocks)
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = b.Name;
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = new Vector3((b.X0 + b.X1) * 0.5f, spec.Height + b.BaseY + b.Height * 0.5f, (b.Z0 + b.Z1) * 0.5f);
                go.transform.localScale = new Vector3(b.X1 - b.X0, b.Height, b.Z1 - b.Z0);
                go.GetComponent<Renderer>().sharedMaterial = BlockMat(b.Material);
            }

            foreach (Plank p in spec.Planks) CreatePlank(root.transform, p);

            PrefabUtility.SaveAsPrefabAsset(root, $"{BuildingFolder}/{spec.Name}.prefab");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    /// <summary>A plank under parent, positioned in parent's space so its top runs from p.From to p.To.</summary>
    static void CreatePlank(Transform parent, Plank p)
    {
        Vector3 along = p.To - p.From;
        Quaternion rotation = Quaternion.LookRotation(along.normalized, Vector3.up);
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = p.Name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = (p.From + p.To) * 0.5f - rotation * Vector3.up * (p.Thickness * 0.5f);
        go.transform.localRotation = rotation;
        go.transform.localScale = new Vector3(p.Width, p.Thickness, along.magnitude);
        go.GetComponent<Renderer>().sharedMaterial = BlockMat(p.Narrow ? "KIT_Pipe" : "KIT_Plank");

        float slope = Mathf.Atan2(Mathf.Abs(along.y), new Vector2(along.x, along.z).magnitude) * Mathf.Rad2Deg;
        if (!p.Narrow && p.Width < PlayerRadius * 2f)
            Debug.LogWarning($"Kit: plank {p.Name} is {p.Width} m wide, narrower than the player ({PlayerRadius * 2f} m).");
        if (slope > 40f)
            Debug.LogWarning($"Kit: plank {p.Name} slopes {slope:0} degrees, too steep to walk up (45 is the limit).");
    }

    /// <summary>Warns about parts the player can't use the way the design intends.</summary>
    static void Check(BuildingSpec spec)
    {
        foreach (Block b in spec.Blocks)
        {
            if (b.Decor) continue;
            float depth = Mathf.Min(b.X1 - b.X0, b.Z1 - b.Z0);
            if (b.BaseY > 0f)
            {
                // Something to get under (a beam): it must clear a crouching player but not a standing one.
                if (b.BaseY < CrouchHeight + 0.1f)
                    Debug.LogWarning($"Kit: {spec.Name}/{b.Name} is {b.BaseY} m off the roof; a crouching player ({CrouchHeight} m) won't fit under.");
                continue;
            }
            if (b.Height > MantleReach)
                Debug.LogWarning($"Kit: {spec.Name}/{b.Name} is {b.Height} m tall, above mantle reach ({MantleReach} m).");
            else if (b.Height > JumpHeight && depth < MinMantleDepth)
                Debug.LogWarning($"Kit: {spec.Name}/{b.Name} is only {depth} m thick; the mantle needs about {MinMantleDepth} m to land on.");
        }
        foreach (Slab s in spec.Slabs)
        {
            if (s.Overhang < PlayerRadius * 2f)
                Debug.LogWarning($"Kit: {spec.Name}/{s.Name} sticks out {s.Overhang} m, less than the player's width " +
                                 $"({PlayerRadius * 2f} m); the player can't stand on it.");
        }
    }

    // ------------------------------------------------------------------ meshes

    /// <summary>Loads the mesh asset at this name and refills it, or creates it, so prefabs keep pointing at it.</summary>
    static Mesh SaveMesh(string name, Action<Mesh> fill)
    {
        string path = $"{MeshFolder}/{name}.asset";
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        bool isNew = mesh == null;
        if (isNew) mesh = new Mesh();
        mesh.Clear();
        mesh.name = name;
        fill(mesh);
        if (isNew) AssetDatabase.CreateAsset(mesh, path);
        else EditorUtility.SetDirty(mesh);
        return mesh;
    }

    /// <summary>
    /// A convex footprint extruded from bottom up to height. Submesh 0 is the top, submesh 1 the sides and bottom.
    /// Faces don't share vertices, so edges stay sharp. UVs are in metres, for grid textures later.
    /// </summary>
    static void BuildPrism(Mesh mesh, Vector2[] footprint, float bottom, float height)
    {
        var verts = new List<Vector3>();
        var uvs = new List<Vector2>();
        var roof = new List<int>();
        var sides = new List<int>();
        int n = footprint.Length;

        Vector2 centre = Vector2.zero;
        foreach (Vector2 p in footprint) centre += p;
        centre /= n;

        // Roof and bottom caps (fans; fine for a convex outline).
        foreach (float y in new[] { height, bottom })
        {
            int start = verts.Count;
            foreach (Vector2 p in footprint)
            {
                verts.Add(new Vector3(p.x, y, p.y));
                uvs.Add(p);
            }
            for (int i = 1; i < n - 1; i++)
            {
                if (y == height) AddTriangle(roof, verts, start, start + i, start + i + 1, Vector3.up);
                else AddTriangle(sides, verts, start, start + i, start + i + 1, Vector3.down);
            }
        }

        // Sides.
        for (int i = 0; i < n; i++)
        {
            Vector2 a = footprint[i], b = footprint[(i + 1) % n];
            Vector2 edge = b - a;
            var outward2 = new Vector2(edge.y, -edge.x);
            if (Vector2.Dot(outward2, (a + b) * 0.5f - centre) < 0f) outward2 = -outward2;
            var outward = new Vector3(outward2.x, 0f, outward2.y);

            int s = verts.Count;
            float length = edge.magnitude;
            verts.Add(new Vector3(a.x, bottom, a.y)); uvs.Add(new Vector2(0f, bottom));
            verts.Add(new Vector3(b.x, bottom, b.y)); uvs.Add(new Vector2(length, bottom));
            verts.Add(new Vector3(b.x, height, b.y)); uvs.Add(new Vector2(length, height));
            verts.Add(new Vector3(a.x, height, a.y)); uvs.Add(new Vector2(0f, height));
            AddTriangle(sides, verts, s, s + 1, s + 2, outward);
            AddTriangle(sides, verts, s, s + 2, s + 3, outward);
        }

        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(roof, 0);
        mesh.SetTriangles(sides, 1);
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
    }

    /// <summary>Adds a triangle wound so its visible side faces <paramref name="outward"/>.</summary>
    static void AddTriangle(List<int> tris, List<Vector3> v, int a, int b, int c, Vector3 outward)
    {
        Vector3 normal = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
        if (Vector3.Dot(normal, outward) >= 0f) { tris.Add(a); tris.Add(b); tris.Add(c); }
        else { tris.Add(a); tris.Add(c); tris.Add(b); }
    }

    // ------------------------------------------------------------------ test scene

    static void BuildTestScene()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.62f, 0.68f, 0.78f);
        RenderSettings.ambientEquatorColor = new Color(0.45f, 0.46f, 0.48f);
        RenderSettings.ambientGroundColor = new Color(0.25f, 0.24f, 0.23f);

        var sun = new GameObject("Directional Light").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.intensity = 1.2f;
        sun.shadows = LightShadows.Soft;
        sun.transform.rotation = Quaternion.Euler(50f, -35f, 0f);

        // Each building at its place in the test layout.
        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (BuildingSpec spec in Specs())
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{BuildingFolder}/{spec.Name}.prefab");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            instance.transform.position = spec.TestPosition;
            var outline = new List<Vector2>(spec.Footprint);
            foreach (Slab s in spec.Slabs) outline.AddRange(s.Outline);
            foreach (Vector2 p in outline)
            {
                minX = Mathf.Min(minX, spec.TestPosition.x + p.x); maxX = Mathf.Max(maxX, spec.TestPosition.x + p.x);
                minZ = Mathf.Min(minZ, spec.TestPosition.z + p.y); maxZ = Mathf.Max(maxZ, spec.TestPosition.z + p.y);
            }
        }

        // Planks between buildings.
        var planks = new GameObject("Planks").transform;
        foreach (Plank p in Planks()) CreatePlank(planks, p);

        // Free-standing structures: billboards to wall-run along.
        var structures = new GameObject("Structures").transform;
        foreach (Structure s in Structures())
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = s.Name;
            go.transform.SetParent(structures, false);
            go.transform.position = (s.Min + s.Max) * 0.5f;
            go.transform.localScale = s.Max - s.Min;
            go.GetComponent<Renderer>().sharedMaterial = BlockMat(s.Material);
        }

        // Checkpoints: touching one makes falls respawn there.
        var checkpoints = new GameObject("Checkpoints").transform;
        foreach (CheckpointSpec c in Checkpoints())
        {
            var go = new GameObject(c.Name);
            go.transform.SetParent(checkpoints, false);
            go.transform.SetPositionAndRotation(c.Feet, Quaternion.Euler(0f, c.Yaw, 0f));
            var trigger = go.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.center = new Vector3(0f, c.Size.y * 0.5f, 0f);
            trigger.size = c.Size;
            go.AddComponent<Checkpoint>();
        }

        var centre = new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);
        var area = new Vector3(maxX - minX + 200f, 1f, maxZ - minZ + 200f);
        GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
        ground.name = "Ground";
        ground.transform.position = centre + Vector3.down * 0.5f;
        ground.transform.localScale = area;
        ground.GetComponent<Renderer>().sharedMaterial = Mat("KIT_Ground", new Color(0.33f, 0.34f, 0.33f));

        var spawn = new GameObject("PlayerStart").transform;
        spawn.position = TestSpawn;
        spawn.rotation = Quaternion.Euler(0f, TestSpawnYaw, 0f);

        // Falling to the street sends you back to the start.
        var killZone = new GameObject("KillZone");
        killZone.transform.position = centre + Vector3.up * 2f;
        var box = killZone.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(area.x, 4f, area.z);
        killZone.AddComponent<KillZone>().SetDefaultSpawn(spawn);

        CopyPlayer(scene, spawn);

        EditorSceneManager.SaveScene(scene, TestScenePath);
    }

    /// <summary>
    /// Copies the Player from SampleScene. Its movement add-ons (wall run, grab, camera bob...) are added on that
    /// scene's instance rather than in the prefab, so a fresh prefab instance would be missing them.
    /// </summary>
    static void CopyPlayer(Scene into, Transform spawn)
    {
        Scene source = EditorSceneManager.OpenScene(PlayerSourceScenePath, OpenSceneMode.Additive);
        try
        {
            GameObject original = null;
            foreach (GameObject go in source.GetRootGameObjects())
                if (go.name == PlayerName) { original = go; break; }
            if (original == null)
                throw new Exception($"Kit: no '{PlayerName}' object in {PlayerSourceScenePath} to copy.");

            SceneManager.SetActiveScene(into);
            GameObject player = Object.Instantiate(original);
            player.name = PlayerName;

            // Move the whole rig so the capsule's feet land on the spawn, cameras and all.
            var controller = player.GetComponentInChildren<CharacterController>(true);
            if (controller == null) throw new Exception("Kit: the copied Player has no CharacterController.");
            Transform capsule = controller.transform;
            Vector3 feet = capsule.position + Vector3.up * (controller.center.y - controller.height * 0.5f);
            player.transform.position += spawn.position - feet + Vector3.up * 0.05f;
            capsule.rotation = spawn.rotation;
        }
        finally
        {
            EditorSceneManager.CloseScene(source, true);   // never saved: SampleScene is left as it was
        }
    }

    // ------------------------------------------------------------------ screenshots

    /// <summary>Renders a few views of the open test scene to PNGs, for checking the build without the editor.</summary>
    static void CaptureShots(string folder)
    {
        Directory.CreateDirectory(folder);
        ShaderUtil.allowAsyncCompilation = false;   // otherwise the first shot renders with the grey placeholder shader
        var look = new Vector3(55f, 15f, 100f);

        // The first render after loading can still use placeholder shaders, so render once and throw it away.
        Shot(folder, "0_warmup", new Vector3(-30f, 90f, 20f), look, 55f);
        File.Delete(Path.Combine(folder, "0_warmup.png"));

        Shot(folder, "01_overview_southwest", new Vector3(-30f, 90f, 20f), look, 55f);
        Shot(folder, "02_top_down", new Vector3(50f, 150f, 90f), new Vector3(50f, 0f, 90.001f), 0f, ortho: 92f);
        Shot(folder, "03_scaffold_from_BLD_06", new Vector3(60f, 13.6f, 50f), new Vector3(61f, 16f, 62f), 80f);
        Shot(folder, "04_wallrun_07_to_08", new Vector3(63f, 21.6f, 75.7f), new Vector3(44f, 20f, 75f), 80f);
        Shot(folder, "05_split_from_BLD_08", new Vector3(44f, 20.6f, 70f), new Vector3(36f, 18f, 92f), 80f);
        Shot(folder, "06_terraces_BLD_10", new Vector3(30f, 20.6f, 98f), new Vector3(36f, 21f, 114f), 80f);
        Shot(folder, "07_scaffold_bridge_11", new Vector3(42f, 25.1f, 119f), new Vector3(60f, 22f, 125f), 80f);
        Shot(folder, "08_ledge_BLD_13", new Vector3(80f, 18.6f, 132f), new Vector3(84f, 19f, 142f), 80f);
        Shot(folder, "09_final_split_from_BLD_13", new Vector3(82f, 24.1f, 141f), new Vector3(84f, 23f, 168f), 80f);
    }

    static void Shot(string folder, string name, Vector3 from, Vector3 at, float fov, float ortho = 0f)
    {
        const int w = 1280, h = 720;
        var go = new GameObject("ShotCamera");
        try
        {
            var cam = go.AddComponent<Camera>();
            cam.transform.position = from;
            cam.transform.rotation = Quaternion.LookRotation(at - from, Mathf.Abs((at - from).normalized.y) > 0.99f ? Vector3.forward : Vector3.up);
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 1000f;
            cam.clearFlags = CameraClearFlags.Skybox;
            if (ortho > 0f) { cam.orthographic = true; cam.orthographicSize = ortho; }
            else cam.fieldOfView = fov;

            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(folder, name + ".png"), tex.EncodeToPNG());

            cam.targetTexture = null;
            Object.DestroyImmediate(tex);
            rt.Release();
            Object.DestroyImmediate(rt);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    // ------------------------------------------------------------------ materials and folders

    static Material BlockMat(string name)
    {
        switch (name)
        {
            case "KIT_Wall": return Mat(name, new Color(0.93f, 0.82f, 0.45f));     // can be mantled / run along
            case "KIT_Storage": return Mat(name, new Color(0.88f, 0.55f, 0.32f));  // launch platform
            case "KIT_Ledge": return Mat(name, new Color(0.95f, 0.66f, 0.3f));     // ledge: a last-chance landing
            case "KIT_Utility": return Mat(name, new Color(0.62f, 0.7f, 0.85f));   // small rooftop utility box
            case "KIT_Fence": return Mat(name, new Color(0.3f, 0.32f, 0.36f));     // thin metal fence: hop over
            case "KIT_Beam": return Mat(name, new Color(0.85f, 0.3f, 0.25f));      // low beam: crouch or slide under
            case "KIT_Post": return Mat(name, new Color(0.4f, 0.42f, 0.45f));      // supports, masts
            case "KIT_Plank": return Mat(name, new Color(0.62f, 0.47f, 0.3f));     // wooden plank between buildings
            case "KIT_Prop": return Mat(name, new Color(0.7f, 0.72f, 0.7f));       // AC units, vents, tanks
            case "KIT_Pipe": return Mat(name, new Color(0.55f, 0.58f, 0.6f));      // narrow pipe to balance on
            case "KIT_Scaffold": return Mat(name, new Color(0.86f, 0.7f, 0.35f));  // scaffold decks
            case "KIT_Billboard": return Mat(name, new Color(0.25f, 0.7f, 0.68f)); // wall-run surfaces are teal
            case "KIT_Raised": return Mat(name, new Color(0.62f, 0.66f, 0.74f));   // upper levels, terraces, penthouses
            case "KIT_CityBlock": return Mat(name, new Color(0.5f, 0.52f, 0.56f)); // walls too tall to climb
            default: return Mat(name, new Color(0.6f, 0.6f, 0.6f));
        }
    }

    static Material Mat(string name, Color color)
    {
        string path = $"{MaterialFolder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.1f);
        EditorUtility.SetDirty(material);
        return material;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
