using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/// <summary>
/// Layout of the walled-city greybox: a 20 x 20 grid of 3 m cells (60 x 60 m, north = +z). Every cell is a solid
/// column; the route is carved into the mass of buildings as alleys, roofs, rooms and stair halls, and every other
/// cell is a filler building tall enough that you can't climb onto it from the route. Plain C# (no UnityEngine) so
/// the route checker can run outside Unity too.
///
/// The route, always heading for higher ground (alternatives in brackets):
///   1. Street level (0 m): in from the south through narrow alleys, two stretches roofed over, a low pipe.
///      [Or cut through the market: open-fronted stalls behind partition walls.]
///   2. Light well 1: a switchback stair up to the rooftops (8 m), then roofs stepping up to 10 and 12 m.
///      [Or the apartment block: lobby, a stair hall up to rooms at 4 m, a second stair hall up to rooms at 8 m, out
///      onto a balcony and over a diagonal plank to the same roofs.]
///   3. Down: drop to a lower roof (7 m), then the dark corridor under the buildings
///      [or the flat beside it: a U of rooms joined by doorways].
///   4. Light well 2 up to 15 m, then the plank bridge over the canyon.
///      [Or from the 12 m roof, climb east and cross the canyon on a narrow diagonal pipe (or jump it) - shorter,
///      riskier, and it skips the corridor and light well 2.]
///   5. The ledge along the canyon (a gap to jump), then roofs up to 19 m.
///      [Or through the rooms on the canyon's east side and up a diagonal stair to the same roofs.]
///   6. Down into the courtyard round the summit tower: drop into the second corridor
///      [or go through two rooms and down an interior stair into a row of rooms beside the corridor; a doorway
///      links the two].
///   7. A stair spiralling round the first tower to its roof (32 m) - halfway.
///   The upper town (rows 20-31):
///   8. A plank north from the tower roof to the upper town's rooftops, through a rooftop shack.
///   9. Down: drop into a tenement (26.5 m), a winding string of rooms, then a stair hall down to 22 m.
///  10. A corridor that has caved in twice: weave out through the rooms on one side and then the other.
///  11. Light well 3 up to 30 m, then the roofs along the west edge up to 33 m.
///  12. Roof-hopping over the upper canyon: gaps to jump and two diagonal planks
///      [or a row of rooms after the first plank].
///  13. Four buildings round a light shaft: climb each inside, then jump out of a window across the gap into the
///      next, a floor higher - A (36 m), B (40 m), C (44 m) - and finally the highest tower, up to its roof pillar
///      (52.5 m) under the red-lit mast.
///
/// Low walls to vault over and low beams to slide under sit across straight stretches of the way
/// (<see cref="AddVaults"/>, <see cref="AddLowBeams"/>), and three slopes to slide down replace drops: off the 12 m roof,
/// along the canyon ledge, and from the 19 m roofs down to the second corridor (<see cref="Slides"/>).
///
/// Way-finding is built into the layout: the final tower carries a tall light beam, and <see cref="OpenSightlines"/>
/// makes sure it comes into view every few cells along the way (at most WorstStepsWithoutView cells apart) by
/// lowering buildings in the line of sight - never below climbing height, and re-checked for shortcuts - or, in a
/// run of rooms, by collapsing a ceiling. Lanterns mark where the way takes a stair or plank, and the floor colour
/// warms as you get closer (<see cref="ComputeGuides"/>).
/// </summary>
public sealed class KowloonLayout
{
    public const int W = 20, D = 32;
    public const float Cell = 3f;
    public const float Headroom = 3.2f;   // clear height of ordinary rooms, covered alleys and corridors
    public const float Climb = 3.4f;      // highest ledge the player can reach: jump (1.2) + mantle (2.3), a bit under
    const float Body = 1.9f;              // clearance the player needs to stand or move (capsule is 2 m)
    const float NoCeiling = float.PositiveInfinity;

    public enum Kind { Filler, Route, Covered, Void }

    /// <summary>Route: walkable top at Floor. Covered: walkable at Floor, ceiling at Ceil, building above it up to
    /// Top. Filler: building, top at Top. Void: open drop to the ground (kill zone).</summary>
    public struct CellData { public Kind Kind; public float Floor, Ceil, Top; public bool Summit; }

    public sealed class Ramp { public string Name; public float X0, Y0, Z0, X1, Y1, Z1, Width; }
    public sealed class Slab { public string Name; public float X, Y, Z, SX, SY, SZ; }   // centre and size (m)
    public sealed class Marker { public string Name; public float X, Y, Z, Yaw; }
    public sealed class Link { public string Name; public int AX, AZ, BX, BZ; public float AY, BY, Yaw; public bool Required; }
    /// <summary>Wall on the shared edge of two cells, from floor to ceiling; Door leaves a doorway in the middle.</summary>
    public sealed class Wall { public int AX, AZ, BX, BZ; public bool Door; }

    public readonly CellData[,] Cells = new CellData[W, D];
    public readonly List<Ramp> Ramps = new List<Ramp>();        // stair flights (top surface start -> end)
    public readonly List<Ramp> PlankRamps = new List<Ramp>();   // planks and pipes, often diagonal
    public readonly List<Ramp> Slides = new List<Ramp>();       // slopes to slide down (top surface start -> end)
    public readonly List<Slab> Landings = new List<Slab>();     // stair landings, pillars and the summit tank
    public readonly List<Slab> Planks = new List<Slab>();       // flat bridges and crates
    public readonly List<Slab> Pipes = new List<Slab>();        // low pipes to crouch under
    public readonly List<Wall> Walls = new List<Wall>();
    public readonly List<Link> Links = new List<Link>();        // what stairs and planks connect (for checking)
    public readonly List<Marker> Checkpoints = new List<Marker>();
    public readonly List<Marker> Lights = new List<Marker>();
    public Marker Spawn;
    public Marker Mast;                                        // the goal's mast (base position)
    public const int StartX = 1, StartZ = 0, GoalX = 15, GoalZ = 29;

    // ------------------------------------------------------------------ the route

    public static KowloonLayout Create()
    {
        var k = new KowloonLayout();

        // 1. Street level. Alleys wind north from the entrance at the south edge; two stretches are roofed over.
        k.Run(1, 0, 1, 5, 0f);
        for (int x = 2; x <= 4; x++) k.Cover(x, 5, 0f, 10f);
        k.Run(5, 2, 5, 5, 0f);
        for (int x = 6; x <= 8; x++) k.Cover(x, 2, 0f, 9f);
        k.Run(9, 2, 9, 7, 0f);
        k.Run(6, 7, 8, 7, 0f);
        k.Open(6, 8, 0f);
        k.Pipe("Pipe_Alley", 9, 5, 0f, true);

        // 1b. The market: stalls open onto the alley, partitions inside, out the far side to the alley.
        for (int x = 2; x <= 4; x++)
            for (int z = 1; z <= 3; z++)
                k.Cover(x, z, 0f, 9f);
        k.AddWall(2, 2, 3, 2, false);
        k.AddWall(3, 2, 3, 3, false);
        k.AddWall(3, 1, 4, 1, true);

        // 2. Light well 1: up the west flight to a landing at 4 m, back down the east flight to the roofs at 8 m.
        k.Run(6, 9, 7, 10, 0f);
        k.Run(6, 11, 7, 11, 4f);
        k.AddRamp("Well1_Flight_A", 19.5f, 0f, 27f, 19.5f, 4f, 33f);
        k.AddRamp("Well1_Flight_B", 22.5f, 4f, 33f, 22.5f, 8f, 27f);
        k.AddLink("light well 1, first flight", 6, 9, 0f, 6, 11, 4f);
        k.AddLink("light well 1, second flight", 7, 11, 4f, 7, 8, 8f);
        k.Run(7, 8, 8, 8, 8f);
        k.Run(8, 9, 8, 10, 10f);
        k.Open(9, 10, 12f);

        // 2b. The apartment block: lobby -> stair hall -> rooms at 4 m -> stair hall -> rooms at 8 m -> balcony ->
        //     diagonal plank over the light well's entrance onto the 8 m roof.
        k.Cover(1, 6, 0f, 15f);
        k.Cover(1, 7, 0f, 15f);
        k.Cover(1, 8, 0f, 15f, 7f);                        // stair hall 1
        k.Cover(1, 9, 0f, 15f, 7f);
        k.AddRamp("Apartment_Stair_1", 4.5f, 0f, 24f, 4.5f, 4f, 30f);
        k.AddLink("apartment stair 1", 1, 9, 0f, 1, 10, 4f);
        foreach (int[] c in new[] { new[] { 1, 10 }, new[] { 2, 10 }, new[] { 1, 11 }, new[] { 2, 11 }, new[] { 3, 11 } })
            k.Cover(c[0], c[1], 4f, 15f);
        k.AddWall(1, 10, 2, 10, true);
        k.AddWall(2, 10, 2, 11, false);
        k.AddWall(2, 11, 3, 11, true);
        k.AddWall(2, 10, 3, 10, false);
        k.Cover(3, 10, 4f, 15f, 11f);                      // stair hall 2
        k.Cover(3, 9, 4f, 15f, 11f);
        k.AddRamp("Apartment_Stair_2", 10.5f, 4f, 33f, 10.5f, 8f, 27f);
        k.AddLink("apartment stair 2", 3, 10, 4f, 3, 8, 8f);
        k.Cover(3, 8, 8f, 15f);
        k.Cover(4, 8, 8f, 15f);
        k.Cover(4, 9, 8f, 15f);
        k.AddWall(3, 8, 4, 8, true);
        k.AddWall(3, 9, 4, 9, false);
        k.Open(5, 9, 8f);                                  // balcony
        k.PlankRamps.Add(new Ramp { Name = "Plank_Balcony", X0 = 16.5f, Y0 = 8f, Z0 = 28.5f, X1 = 22.5f, Y1 = 8f, Z1 = 25.5f, Width = 2f });
        k.AddLink("balcony plank", 5, 9, 8f, 7, 8, 8f);

        // 3. Drop to the lower roof, then the corridor under the buildings, or the flat beside it.
        // A collapsed roof to slide down from the 12 m roof onto the lower roof (columns stepped under it).
        k.Open(10, 10, 9.5f);
        k.Open(10, 11, 7f);
        k.Open(10, 12, 7f);
        k.AddSlide("Slide_Collapsed_Roof", 31.5f, 12f, 30f, 31.5f, 7f, 36f);
        k.AddLink("collapsed roof, crossing its top", 9, 10, 12f, 11, 10, 13f);
        k.Cover(10, 13, 7f, 14f);
        k.Cover(10, 14, 7f, 14f);
        k.Cover(11, 14, 7f, 14f);
        k.Pipe("Pipe_Corridor_1", 10, 13, 7f, true);
        foreach (int[] c in new[] { new[] { 9, 12 }, new[] { 8, 12 }, new[] { 8, 13 }, new[] { 8, 14 }, new[] { 9, 14 } })
            k.Cover(c[0], c[1], 7f, 14f);
        k.AddWall(10, 12, 9, 12, true);
        k.AddWall(9, 12, 8, 12, true);
        k.AddWall(8, 12, 8, 13, true);
        k.AddWall(8, 13, 8, 14, true);
        k.AddWall(8, 14, 9, 14, true);
        k.AddWall(9, 14, 10, 14, true);

        // 4. Light well 2: same idea, from 7 m up to the roof at 15 m.
        k.Run(11, 15, 12, 16, 7f);
        k.Run(11, 17, 12, 17, 11f);
        k.AddRamp("Well2_Flight_A", 34.5f, 7f, 45f, 34.5f, 11f, 51f);
        k.AddRamp("Well2_Flight_B", 37.5f, 11f, 51f, 37.5f, 15f, 45f);
        k.AddLink("light well 2, first flight", 11, 15, 7f, 11, 17, 11f);
        k.AddLink("light well 2, second flight", 12, 17, 11f, 12, 14, 15f);
        k.Open(12, 14, 15f);

        // 4b. Shortcut: from the 12 m roof climb east and cross the canyon on a narrow diagonal pipe (or jump it).
        k.Open(11, 10, 13f);
        k.Open(11, 9, 14f);
        k.Open(12, 9, 14f);
        k.PlankRamps.Add(new Ramp { Name = "Pipe_Canyon", X0 = 37.5f, Y0 = 14f, Z0 = 28.5f, X1 = 43.5f, Y1 = 13f, Z1 = 25.5f, Width = 0.9f });
        k.AddLink("canyon pipe", 12, 9, 14f, 14, 8, 13f);

        // 5. The canyon, the plank bridge over it, the ledge along it (with a gap) and the roofs up to 19 m.
        for (int z = 1; z <= 18; z++) k.Void(13, z);
        k.Planks.Add(new Slab { Name = "Bridge_Canyon", X = 40.5f, Y = 15f - 0.125f, Z = 43.5f, SX = 4.4f, SY = 0.25f, SZ = 2.4f });
        k.AddLink("canyon bridge", 12, 14, 15f, 14, 14, 14.5f);
        // The ledge slopes down towards the gap (15 -> 13 m) so you can slide and carry speed into the jump.
        k.Open(14, 14, 14.5f);
        k.Open(14, 13, 14f);
        k.Open(14, 12, 13.5f);
        k.Open(14, 11, 13f);
        k.AddSlide("Slide_Ledge", 43.5f, 15f, 45f, 43.5f, 13f, 33f);
        k.Void(14, 10);
        k.Run(14, 7, 14, 9, 13f);
        k.Open(15, 7, 15f);
        k.Open(16, 7, 17f);
        k.Run(16, 8, 16, 9, 19f);
        // S2: a steep slope from the 19 m roofs down to the second corridor's level (columns stepped under it).
        k.Open(16, 10, 16.5f);
        k.Open(16, 11, 14f);
        k.AddSlide("Slide_To_Corridor", 49.5f, 19f, 30f, 49.5f, 14f, 36f);
        k.Open(16, 12, 14f);                               // run-out into the corridor's side

        // 5b. Or the rooms on the canyon's east side, then a diagonal stair up to the 19 m roofs.
        foreach (int[] c in new[] { new[] { 14, 6 }, new[] { 14, 5 }, new[] { 15, 5 }, new[] { 15, 6 } })
            k.Cover(c[0], c[1], 15f, 21f);
        k.AddWall(14, 6, 13, 6, false);
        k.AddWall(14, 5, 13, 5, false);
        k.AddWall(14, 6, 15, 6, false);
        k.AddWall(14, 5, 15, 5, true);
        k.AddWall(15, 6, 16, 6, false);
        for (int x = 16; x <= 17; x++)
            for (int z = 5; z <= 6; z++)
                k.Cover(x, z, 15f, 24f, 22f);              // stair hall
        k.AddRamp("East_Diagonal_Stair", 48.5f, 15f, 16.5f, 53f, 19f, 21f, 2f);
        k.AddLink("east diagonal stair", 16, 5, 15f, 17, 7, 19f);
        k.Open(17, 7, 19f);
        k.Open(17, 8, 19f);

        // 6. Drop into the second corridor, out into the courtyard round the summit tower.
        k.Open(17, 11, 14f);
        for (int z = 12; z <= 14; z++) k.Cover(17, z, 14f, 22f);
        k.Pipe("Pipe_Corridor_2", 17, 14, 14f, true);
        k.Open(17, 15, 14f);
        k.Run(16, 16, 19, 19, 14f);

        // 6b. Or through two rooms and down an interior stair into the rooms beside the corridor.
        k.Cover(17, 9, 18.5f, 23f);
        k.Cover(18, 9, 18.5f, 23f);
        k.AddWall(17, 9, 18, 9, true);
        k.Cover(18, 10, 14f, 24f, 22f);                    // stair hall
        k.Cover(18, 11, 14f, 24f, 22f);
        k.AddWall(17, 11, 18, 11, false);
        k.AddRamp("Stair_Down_To_Courtyard", 55.5f, 18.5f, 30f, 55.5f, 14f, 36f);
        k.AddLink("stair down to the courtyard", 18, 9, 18.5f, 18, 12, 14f);
        for (int z = 12; z <= 14; z++) k.Cover(18, z, 14f, 22f);
        k.AddWall(17, 12, 18, 12, false);
        k.AddWall(17, 13, 18, 13, true);
        k.AddWall(17, 14, 18, 14, false);
        k.Open(18, 15, 14f);

        // 7. The summit tower and the stair spiralling round it (14 -> 18.5 -> 23 -> 27.5 -> 32 m).
        for (int x = 17; x <= 18; x++)
            for (int z = 17; z <= 18; z++)
                k.Cells[x, z] = new CellData { Kind = Kind.Route, Floor = 32f, Ceil = NoCeiling, Top = 32f, Summit = true };
        k.AddRamp("Spiral_1_South", 51f, 14f, 49.5f, 57f, 18.5f, 49.5f);
        k.AddLanding("Spiral_Landing_SE", 19, 16, 18.5f, 14f);
        k.AddRamp("Spiral_2_East", 58.5f, 18.5f, 51f, 58.5f, 23f, 57f);
        k.AddLanding("Spiral_Landing_NE", 19, 19, 23f, 14f);
        k.AddRamp("Spiral_3_North", 57f, 23f, 58.5f, 51f, 27.5f, 58.5f);
        k.AddLanding("Spiral_Landing_NW", 16, 19, 27.5f, 14f);
        k.AddRamp("Spiral_4_West", 49.5f, 27.5f, 57f, 49.5f, 32f, 51f);
        k.AddLanding("Spiral_Landing_Top", 16, 16, 32f, 14f);
        k.AddLink("spiral stair", 16, 16, 14f, 17, 17, 32f, true, 90f);
        k.Landings.Add(new Slab { Name = "Water_Tank", X = 55f, Y = 32f + 1.75f, Z = 55f, SX = 3f, SY = 3.5f, SZ = 3f });
        k.Planks.Add(new Slab { Name = "Crate", X = 52.7f, Y = 32f + 0.9f, Z = 55f, SX = 1.6f, SY = 1.8f, SZ = 1.6f });

        // ================================================================ the upper town (rows 20-31)

        // 8. A plank from the first tower's roof north to the upper town's rooftops, through a rooftop shack.
        k.Planks.Add(new Slab { Name = "Bridge_Tower", X = 52.5f, Y = 32f - 0.125f, Z = 58.5f, SX = 2.4f, SY = 0.25f, SZ = 4.4f });
        k.AddLink("tower bridge", 17, 18, 32f, 17, 20, 32f);
        k.Open(17, 20, 32f);
        k.Open(16, 20, 32f);
        k.Cover(16, 21, 32f, 35f);
        k.AddWall(16, 20, 16, 21, true);
        k.AddWall(16, 21, 17, 21, false);
        k.AddWall(16, 21, 15, 21, true);
        k.Open(15, 21, 32f);

        // 9. Down: drop into the tenement (26.5 m), a winding string of rooms, then a stair hall down to 22 m.
        k.Open(14, 21, 26.5f);
        foreach (int[] c in new[] { new[] { 14, 22 }, new[] { 13, 22 }, new[] { 12, 22 }, new[] { 12, 21 }, new[] { 11, 21 } })
            k.Cover(c[0], c[1], 26.5f, 34f);
        k.AddWall(14, 21, 14, 22, true);
        k.AddWall(14, 22, 13, 22, true);
        k.AddWall(13, 22, 12, 22, true);
        k.AddWall(12, 22, 12, 21, true);
        k.AddWall(12, 21, 11, 21, true);
        k.AddWall(14, 21, 13, 21, false);
        k.Cover(10, 21, 22f, 34f, 30f);                    // stair hall
        k.Cover(9, 21, 22f, 34f, 30f);
        k.AddWall(11, 21, 10, 21, true);
        k.AddRamp("Tenement_Stair", 33f, 26.5f, 64.5f, 27f, 22f, 64.5f);
        k.AddLink("tenement stair", 11, 21, 26.5f, 8, 21, 22f);

        // 10. The 22 m corridor. It has caved in twice, so you weave out through the rooms on one side, back into the
        //     corridor, then through the rooms on the other side.
        foreach (int[] c in new[]
                 {
                     new[] { 8, 21 }, new[] { 8, 22 }, new[] { 7, 22 }, new[] { 6, 22 }, new[] { 5, 22 }, new[] { 4, 22 },
                     new[] { 7, 21 }, new[] { 6, 21 }, new[] { 6, 23 }, new[] { 5, 23 },
                 })
            k.Cover(c[0], c[1], 22f, 30f);
        k.AddWall(8, 21, 7, 21, false);
        k.AddWall(7, 22, 6, 22, false);                    // caved in
        k.AddWall(7, 22, 7, 21, true);
        k.AddWall(7, 21, 6, 21, true);
        k.AddWall(6, 21, 6, 22, true);
        k.AddWall(6, 22, 5, 22, false);                    // caved in
        k.AddWall(6, 22, 6, 23, true);
        k.AddWall(6, 23, 5, 23, true);
        k.AddWall(5, 23, 5, 22, true);
        k.AddWall(5, 23, 5, 24, false);
        k.AddWall(6, 23, 6, 24, false);
        k.Pipe("Pipe_Corridor_3", 4, 22, 22f, false);
        k.Open(3, 22, 22f);

        // 11. Light well 3: from 22 m up to the roofs at 30 m, then north along the roofs at the west edge.
        k.Run(2, 23, 3, 24, 22f);
        k.Run(2, 25, 3, 25, 26f);
        k.AddRamp("Well3_Flight_A", 10.5f, 22f, 69f, 10.5f, 26f, 75f);
        k.AddRamp("Well3_Flight_B", 7.5f, 26f, 75f, 7.5f, 30f, 69f);
        k.AddLink("light well 3, first flight", 3, 23, 22f, 3, 25, 26f);
        k.AddLink("light well 3, second flight", 2, 25, 26f, 2, 22, 30f);
        k.Open(2, 22, 30f);
        k.Open(1, 22, 30f);
        k.Run(1, 23, 1, 24, 30f);
        k.Open(1, 25, 31f);
        k.Open(1, 26, 32f);
        k.Run(2, 26, 3, 26, 32f);

        // 12. Roof-hopping over the upper canyon: gaps to jump and two diagonal planks...
        for (int x = 5; x <= 18; x++) k.Void(x, 24);
        for (int x = 5; x <= 13; x++) k.Void(x, 25);
        foreach (int[] c in new[]
                 {
                     new[] { 5, 26 }, new[] { 7, 26 }, new[] { 12, 26 }, new[] { 5, 27 }, new[] { 6, 27 }, new[] { 7, 27 },
                     new[] { 8, 27 }, new[] { 10, 27 }, new[] { 11, 27 }, new[] { 12, 27 }, new[] { 7, 28 }, new[] { 10, 28 },
                     new[] { 12, 28 },
                 })
            k.Void(c[0], c[1]);
        k.Open(4, 26, 33f);
        k.Open(6, 26, 34f);
        k.PlankRamps.Add(new Ramp { Name = "Plank_Diagonal_1", X0 = 19.5f, Y0 = 34f, Z0 = 79.5f, X1 = 25.5f, Y1 = 34f, Z1 = 85.5f, Width = 2f });
        k.AddLink("first diagonal plank", 6, 26, 34f, 8, 28, 34f);
        k.Open(8, 28, 34f);
        k.Open(9, 28, 35f);
        k.Open(11, 28, 35f);
        k.PlankRamps.Add(new Ramp { Name = "Plank_Diagonal_2", X0 = 34.5f, Y0 = 35f, Z0 = 85.5f, X1 = 40.5f, Y1 = 36f, Z1 = 79.5f, Width = 2f });
        k.AddLink("second diagonal plank", 11, 28, 35f, 13, 26, 36f);
        k.Open(13, 26, 36f);

        //     ...or, after the first plank, through a row of rooms that turns north into building A's side.
        for (int x = 9; x <= 13; x++) k.Cover(x, 29, 36f, 39.3f);
        k.Cover(13, 28, 36f, 39.3f);
        k.Cover(13, 27, 36f, 39.3f);
        k.AddWall(9, 28, 9, 29, true);
        k.AddWall(10, 29, 11, 29, true);
        k.AddWall(12, 29, 13, 29, true);
        k.AddWall(10, 28, 10, 29, false);
        k.AddWall(11, 28, 11, 29, false);
        k.AddWall(12, 28, 12, 29, false);
        k.AddWall(13, 29, 13, 28, true);
        k.AddWall(13, 28, 13, 27, true);
        k.AddWall(13, 27, 13, 26, true);
        k.AddWall(12, 28, 13, 28, false);
        k.AddWall(12, 27, 13, 27, false);
        k.AddWall(13, 28, 14, 28, false);

        // 13. Four buildings round a light shaft (a 3 m gap all round, open to the ground). Climb each one inside, then
        //     jump out of a window across the gap into the next one, a floor higher, until the highest tower:
        //       A (south-west, 36 -> 39 m)  ->  east window  ->  B (south-east, 40 -> 43 m)  ->  north window  ->
        //       C (north-east, 44 -> 47 m)  ->  west window  ->  D, the highest tower (north-west, 48 -> 52.5 m roof).
        //     Each building: a two-cell room you arrive in, beside a two-cell stair hall with a 2 m ramp (the strip
        //     beside it takes you to its foot). The towers stand above the buildings round them.
        foreach (int[] c in new[]
                 {
                     new[] { 16, 26 }, new[] { 16, 27 }, new[] { 16, 28 }, new[] { 16, 29 }, new[] { 16, 30 },
                     new[] { 14, 28 }, new[] { 15, 28 }, new[] { 17, 28 }, new[] { 18, 28 },
                 })
            k.Void(c[0], c[1]);

        // A: in from the west (the roof, or the rooms' side door), up the ramp, east into the upper room, out its window.
        k.Tower(14, 26, 36f, 42.2f, 43f);                 // stair hall
        k.Tower(14, 27, 36f, 42.2f, 43f);
        k.Tower(15, 26, 39f, 42.2f, 43f);                 // upper room
        k.Tower(15, 27, 39f, 42.2f, 43f);
        k.AddRamp("Building_A_Stair", 44f, 36f, 78f, 44f, 39f, 84f, 2f);
        k.AddLink("building A's stair", 14, 26, 36f, 15, 27, 39f);
        k.WallRound(14, 26, 15, 27, new[] { 13, 26, 14, 26 }, new[] { 13, 27, 14, 27 }, new[] { 15, 27, 16, 27 });
        k.AddLink("jump from building A to B", 15, 27, 39f, 17, 27, 40f, true);

        // B: in through the west window, along to the ramp's foot, up it and straight out of the north window.
        k.Tower(17, 26, 40f, 43.2f, 47f);                 // room
        k.Tower(17, 27, 40f, 43.2f, 47f);
        k.Tower(18, 26, 40f, 46.2f, 47f);                 // stair hall
        k.Tower(18, 27, 40f, 46.2f, 47f);
        k.AddRamp("Building_B_Stair", 56f, 40f, 78f, 56f, 43f, 84f, 2f);
        k.WallRound(17, 26, 18, 27, new[] { 17, 27, 16, 27 }, new[] { 18, 27, 18, 28 });
        k.AddLink("building B's stair and the jump to C", 17, 26, 40f, 18, 29, 44f, true);

        // C: in through the south window, up the ramp along the west wall, out of the west window near its top.
        k.Tower(18, 29, 44f, 47.2f, 51f);                 // room
        k.Tower(18, 30, 44f, 47.2f, 51f);
        k.Tower(17, 29, 44f, 50.2f, 51f);                 // stair hall
        k.Tower(17, 30, 44f, 50.2f, 51f);
        k.AddRamp("Building_C_Stair", 52f, 44f, 87f, 52f, 47f, 93f, 2f);
        k.WallRound(17, 29, 18, 30, new[] { 18, 29, 18, 28 }, new[] { 17, 30, 16, 30 });
        k.AddLink("building C's stair and the jump to the highest tower", 18, 29, 44f, 15, 30, 48f, true);

        // D, the highest tower: in through the east window, up a ramp in the open-topped hall, across onto the roof
        // pillar under the mast (the goal).
        k.Tower(15, 30, 48f, 51.2f, 52.5f);               // room
        k.Tower(14, 30, 48f, 53f, 53f);                   // stair hall, open to the sky
        k.Tower(14, 29, 48f, 53f, 53f);
        k.Cells[15, 29] = new CellData { Kind = Kind.Route, Floor = 52.5f, Ceil = NoCeiling, Top = 52.5f, Summit = true };
        k.AddRamp("Highest_Tower_Stair", 43f, 48f, 93f, 43f, 52f, 87f, 2f);
        k.WallRound(14, 29, 15, 30, new[] { 15, 30, 16, 30 });
        k.AddLink("highest tower's stair", 15, 30, 48f, 15, 29, 52.5f, true, 0f);
        k.Mast = new Marker { Name = "Mast", X = 47.6f, Y = 52.5f, Z = 89.6f };
        // Checkpoints (respawn points), lights and the start.
        k.Spawn = new Marker { Name = "PlayerSpawn", X = 4.5f, Y = 0.05f, Z = 1.5f, Yaw = 0f };
        k.Checkpoint("CP_Start", 1, 0, 0f, 0f);
        k.Checkpoint("CP_Rooftops", 8, 9, 10f, 0f);
        k.Checkpoint("CP_Apartments", 4, 8, 8f, 90f);
        k.Checkpoint("CP_LowerRoof", 10, 12, 7f, 0f);
        k.Checkpoint("CP_Well2_Top", 12, 14, 15f, 90f);
        k.Checkpoint("CP_Ledge", 14, 8, 13f, 180f);
        k.Checkpoint("CP_East_Rooms", 15, 5, 15f, 90f);
        k.Checkpoint("CP_Corridor_2", 17, 11, 14f, 0f);
        k.Checkpoint("CP_Side_Rooms", 18, 12, 14f, 0f);
        k.Checkpoint("CP_Courtyard", 16, 16, 14f, 90f);
        k.Checkpoints.Add(new Marker { Name = "CP_First_Tower", X = 54f, Y = 32f, Z = 52.5f });
        k.Checkpoint("CP_Upper_Town", 16, 20, 32f, 270f);
        k.Checkpoint("CP_Tenement", 14, 21, 26.5f, 0f);
        k.Checkpoint("CP_Corridor_3", 8, 22, 22f, 270f);
        k.Checkpoint("CP_Well3_Top", 1, 22, 30f, 0f);
        k.Checkpoint("CP_Canyon_Roofs", 4, 26, 33f, 90f);
        k.Checkpoint("CP_Before_Building_A", 13, 26, 36f, 90f);
        k.Checkpoint("CP_Building_B", 17, 27, 40f, 90f);
        k.Checkpoint("CP_Building_C", 18, 29, 44f, 0f);
        k.Checkpoint("CP_Highest_Tower", 15, 30, 48f, 270f);
        k.Checkpoint("CP_Goal", 15, 29, 52.5f, 0f);
        foreach (int[] c in new[]
                 {
                     new[] { 1, 3 }, new[] { 3, 5 }, new[] { 7, 2 }, new[] { 9, 5 }, new[] { 6, 10 }, new[] { 10, 13 },
                     new[] { 11, 16 }, new[] { 17, 13 }, new[] { 18, 16 }, new[] { 3, 2 }, new[] { 1, 7 }, new[] { 2, 11 },
                     new[] { 4, 8 }, new[] { 8, 13 }, new[] { 15, 5 }, new[] { 17, 6 }, new[] { 18, 10 }, new[] { 18, 13 },
                     new[] { 16, 21 }, new[] { 13, 22 }, new[] { 11, 21 }, new[] { 8, 22 }, new[] { 6, 21 }, new[] { 5, 23 },
                     new[] { 2, 24 }, new[] { 11, 29 }, new[] { 13, 28 }, new[] { 15, 26 }, new[] { 17, 27 }, new[] { 18, 30 },
                     new[] { 15, 30 },
                 })
            k.Lights.Add(new Marker { Name = "Light", X = Centre(c[0]), Y = k.Cells[c[0], c[1]].Floor + 2.4f, Z = Centre(c[1]) });
        k.FillBuildings();
        k.ComputeGuides();
        k.AddVaults();
        k.AddLowBeams();
        k.OpenSightlines();
        k.ComputeGuides();
        k.UpdateViews();
        return k;
    }

    // ------------------------------------------------------------------ authoring helpers

    public static float Centre(int cell) => cell * Cell + Cell * 0.5f;
    static bool In(int x, int z) => x >= 0 && x < W && z >= 0 && z < D;

    void Open(int x, int z, float h) => Cells[x, z] = new CellData { Kind = Kind.Route, Floor = h, Ceil = NoCeiling, Top = h };
    void Void(int x, int z) => Cells[x, z] = new CellData { Kind = Kind.Void, Ceil = NoCeiling };

    /// <summary>Indoor cell: floor, building above up to top; ceiling at floor + Headroom unless given (stair halls).</summary>
    void Cover(int x, int z, float floor, float top, float ceil = -1f) =>
        Cells[x, z] = new CellData { Kind = Kind.Covered, Floor = floor, Ceil = ceil > 0f ? ceil : floor + Headroom, Top = top };

    void Run(int x0, int z0, int x1, int z1, float h)
    {
        for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
            for (int z = Math.Min(z0, z1); z <= Math.Max(z0, z1); z++)
                Open(x, z, h);
    }

    void AddRamp(string name, float x0, float y0, float z0, float x1, float y1, float z1, float width = Cell) =>
        Ramps.Add(new Ramp { Name = name, X0 = x0, Y0 = y0, Z0 = z0, X1 = x1, Y1 = y1, Z1 = z1, Width = width });

    /// <summary>A slope to slide down (or walk back up), full cell width.</summary>
    void AddSlide(string name, float x0, float y0, float z0, float x1, float y1, float z1) =>
        Slides.Add(new Ramp { Name = name, X0 = x0, Y0 = y0, Z0 = z0, X1 = x1, Y1 = y1, Z1 = z1, Width = Cell });
    /// <summary>A landing slab filling a cell at height top, on a pillar at its outer corner down to floor.</summary>
    void AddLanding(string name, int x, int z, float top, float floor)
    {
        Landings.Add(new Slab { Name = name, X = Centre(x), Y = top - 0.2f, Z = Centre(z), SX = Cell, SY = 0.4f, SZ = Cell });
        const float towerCentre = 54f;
        float px = Centre(x) + Math.Sign(Centre(x) - towerCentre) * 1.2f, pz = Centre(z) + Math.Sign(Centre(z) - towerCentre) * 1.2f;
        float h = top - 0.4f - floor;
        Landings.Add(new Slab { Name = name + "_Pillar", X = px, Y = floor + h * 0.5f, Z = pz, SX = 0.5f, SY = h, SZ = 0.5f });
    }

    /// <summary>What a stair or plank connects. yaw: direction you set off in, if not straight at the far end.</summary>
    void AddLink(string name, int ax, int az, float ay, int bx, int bz, float by, bool required = false, float yaw = float.NaN) =>
        Links.Add(new Link { Name = name, AX = ax, AZ = az, AY = ay, BX = bx, BZ = bz, BY = by, Required = required, Yaw = yaw });

    void AddWall(int ax, int az, int bx, int bz, bool door) => Walls.Add(new Wall { AX = ax, AZ = az, BX = bx, BZ = bz, Door = door });

    /// <summary>A cell of one of the final buildings: indoors (floor, ceiling, roof top), standing above the city
    /// (the buildings round it aren't raised to match).</summary>
    void Tower(int x, int z, float floor, float ceil, float top)
    {
        Cover(x, z, floor, top, ceil);
        Cells[x, z].Summit = true;
    }

    /// <summary>Walls round the outside of the building covering cells x0..x1, z0..z1; each opening (cell x, z,
    /// outside x, z) is a full-height doorway or window instead.</summary>
    void WallRound(int x0, int z0, int x1, int z1, params int[][] openings)
    {
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + DX[d], nz = z + DZ[d];
                    if (nx >= x0 && nx <= x1 && nz >= z0 && nz <= z1) continue;   // inside the building
                    bool open = false;
                    foreach (int[] o in openings)   // either way round: (building cell, outside) or (outside, building cell)
                        if ((o[0] == x && o[1] == z && o[2] == nx && o[3] == nz) || (o[0] == nx && o[1] == nz && o[2] == x && o[3] == z)) open = true;
                    if (!open && Cells[x, z].Kind != Kind.Covered) continue;   // a solid column needs no wall
                    AddWall(x, z, nx, nz, open);
                }
    }
    /// <summary>A pipe across a cell at crouch height (bottom 1.35 m above the floor).</summary>
    void Pipe(string name, int x, int z, float floor, bool alongX) =>
        Pipes.Add(new Slab { Name = name, X = Centre(x), Y = floor + 1.5f, Z = Centre(z), SX = alongX ? Cell : 0.3f, SY = 0.3f, SZ = alongX ? 0.3f : Cell });

    void Checkpoint(string name, int x, int z, float y, float yaw) =>
        Checkpoints.Add(new Marker { Name = name, X = Centre(x), Y = y, Z = Centre(z), Yaw = yaw });

    /// <summary>
    /// Every cell not on the route becomes a building at least 4 m above any route surface within two cells (more than
    /// you can climb or jump up to), plus some variation so the skyline is ragged. The summit tower is left out so
    /// the buildings round it stay lower than it.
    /// </summary>
    void FillBuildings()
    {
        for (int x = 0; x < W; x++)
            for (int z = 0; z < D; z++)
            {
                if (Cells[x, z].Kind != Kind.Filler || Cells[x, z].Top > 0f) continue;
                float need = 0f;
                for (int dx = -2; dx <= 2; dx++)
                    for (int dz = -2; dz <= 2; dz++)
                    {
                        int nx = x + dx, nz = z + dz;
                        if (!In(nx, nz)) continue;
                        CellData n = Cells[nx, nz];
                        need = Math.Max(need, SlideTop(nx, nz) + 4f);   // you can stand partway down a slope
                        if (n.Kind == Kind.Filler || n.Summit) continue;
                        need = Math.Max(need, (n.Kind == Kind.Covered ? n.Top : n.Floor) + 4f);
                    }
                float noise = Hash(x, z);
                float h = Math.Max(need + (float)Math.Round(noise * 6f) * 0.5f, 12f + (float)Math.Round(noise * 24f) * 0.5f);
                Cells[x, z] = new CellData { Kind = Kind.Filler, Floor = h, Ceil = NoCeiling, Top = h };
            }
    }

    float[,] slideTops;

    /// <summary>Highest point of any slide-down slope over cell (x, z), or -100 if none.</summary>
    float SlideTop(int x, int z)
    {
        if (slideTops == null)
        {
            slideTops = new float[W, D];
            for (int i = 0; i < W; i++) for (int j = 0; j < D; j++) slideTops[i, j] = -100f;
            foreach (Ramp r in Slides)
            {
                float dx = r.X1 - r.X0, dz = r.Z1 - r.Z0, len = (float)Math.Sqrt(dx * dx + dz * dz);
                float sx = -dz / len, sz = dx / len;   // across the slope
                for (float t = 0f; t <= 1.001f; t += 0.02f)
                    for (float w = -r.Width / 2f; w <= r.Width / 2f + 0.001f; w += r.Width / 4f)
                    {
                        int cx = (int)Math.Floor((r.X0 + dx * t + sx * w * 0.98f) / Cell), cz = (int)Math.Floor((r.Z0 + dz * t + sz * w * 0.98f) / Cell);
                        if (In(cx, cz)) slideTops[cx, cz] = Math.Max(slideTops[cx, cz], r.Y0 + (r.Y1 - r.Y0) * t);
                    }
            }
        }
        return slideTops[x, z];
    }
    static float Hash(int x, int z)
    {
        unchecked
        {
            uint h = (uint)(x * 73856093) ^ (uint)(z * 19349663);
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            return (h & 0xFFFF) / 65535f;
        }
    }

    // ------------------------------------------------------------------ low walls to vault

    /// <summary>Low walls across the way (vault or mantle over them), placed on straight, level stretches.</summary>
    public readonly List<Slab> Vaults = new List<Slab>();
    const float VaultHeight = 1.0f, VaultDepth = 0.5f;
    const int VaultSpacing = 3;   // at least this many cells apart

    /// <summary>
    /// Puts a low wall across cells where the way to the goal runs straight through on the level: wall to wall in
    /// alleys and corridors (you have to vault), free-standing where the side is open (vault or go round). Only where
    /// standing on it can't help you climb anything - buildings beside it are raised if needed - and never by stairs,
    /// planks, pipes, drops or doorways.
    /// </summary>
    void AddVaults()
    {
        Vaults.Clear();
        var busy = new HashSet<int>();
        void Mark(float x0, float z0, float x1, float z1, int margin = 1)
        {
            for (int x = (int)Math.Floor(Math.Min(x0, x1) / Cell) - margin; x <= (int)Math.Floor(Math.Max(x0, x1) / Cell) + margin; x++)
                for (int z = (int)Math.Floor(Math.Min(z0, z1) / Cell) - margin; z <= (int)Math.Floor(Math.Max(z0, z1) / Cell) + margin; z++)
                    busy.Add(x * 100 + z);
        }
        // Stairs and planks: the cells they cross, plus the cell just past each end where you get on and off.
        foreach (Ramp r in Ramps.Concat(PlankRamps).Concat(Slides))
        {
            Mark(r.X0, r.Z0, r.X1, r.Z1, 0);
            float dx = r.X1 - r.X0, dz = r.Z1 - r.Z0, len = (float)Math.Sqrt(dx * dx + dz * dz);
            float ux = dx / len * 1.6f, uz = dz / len * 1.6f;
            Mark(r.X0 - ux, r.Z0 - uz, r.X0 - ux, r.Z0 - uz, 0);
            Mark(r.X1 + ux, r.Z1 + uz, r.X1 + ux, r.Z1 + uz, 0);
        }
        foreach (Slab b in Landings.Concat(Planks)) Mark(b.X - b.SX / 2, b.Z - b.SZ / 2, b.X + b.SX / 2, b.Z + b.SZ / 2, 1);
        foreach (Slab b in Pipes) Mark(b.X, b.Z, b.X, b.Z, 0);

        var placed = new List<int[]>();
        var pipeCells = new List<int[]>();   // crouch pipes: not right next to one
        foreach (Slab o in Pipes) pipeCells.Add(new[] { (int)Math.Floor(o.X / Cell), (int)Math.Floor(o.Z / Cell) });
        int beams = 0;
        var order = new List<Node>(bestNext.Keys);
        order.Sort((a, b) => movesLeft[b].CompareTo(movesLeft[a]));
        foreach (Node n in order)
        {
            if (!Walkable(n) || n.X == StartX && n.Z == StartZ || busy.Contains(n.X * 100 + n.Z)) continue;
            Node next = bestNext[n];
            if (!Walkable(next) || Math.Abs(next.X - n.X) + Math.Abs(next.Z - n.Z) != 1) continue;
            int dx = next.X - n.X, dz = next.Z - n.Z;
            int bx = n.X - dx, bz = n.Z - dz;   // the cell straight behind: the way runs through, not round a corner
            if (!In(bx, bz) || !Walkable(new Node(bx, bz, false))) continue;
            CellData c = Cells[n.X, n.Z];
            if (Math.Abs(Cells[next.X, next.Z].Floor - c.Floor) > 0.1f || Math.Abs(Cells[bx, bz].Floor - c.Floor) > 0.1f) continue;
            if (WallBetween(n.X, n.Z, next.X, next.Z, c.Floor) || WallBetween(n.X, n.Z, bx, bz, c.Floor)) continue;
            if (HasWall(n.X, n.Z, next.X, next.Z) || HasWall(n.X, n.Z, bx, bz)) continue;   // doorways
            bool tooClose = false;
            foreach (int[] p in placed) if (Math.Abs(p[0] - n.X) + Math.Abs(p[1] - n.Z) < VaultSpacing) tooClose = true;
            foreach (int[] p in pipeCells) if (Math.Abs(p[0] - n.X) + Math.Abs(p[1] - n.Z) < 2) tooClose = true;
            if (tooClose) continue;

            // Alternate with low beams to slide under, where one fits wall to wall.
            float beamTop = c.Floor + LowBeamBottom + LowBeamThickness;
            bool beamFits = (c.Kind != Kind.Covered || c.Ceil >= beamTop + 0.3f)
                && PipeEndSupported(n.X, n.Z, n.X + dz, n.Z + dx, beamTop) && PipeEndSupported(n.X, n.Z, n.X - dz, n.Z - dx, beamTop);
            if (beamFits && placed.Count % 2 == 1)
            {
                bool along = dx == 0;
                Pipes.Add(new Slab
                {
                    Name = $"Low_Beam_{++beams}", X = Centre(n.X), Y = c.Floor + LowBeamBottom + LowBeamThickness / 2f, Z = Centre(n.Z),
                    SX = along ? Cell : LowBeamDepth, SY = LowBeamThickness, SZ = along ? LowBeamDepth : Cell,
                });
                placed.Add(new[] { n.X, n.Z });
                continue;
            }

            // Indoors the ceiling stops you climbing from its top; outdoors check what's either side.
            bool indoors = c.Kind == Kind.Covered;
            if (indoors && c.Ceil - c.Floor < VaultHeight + 2.1f) continue;   // room to stand up on it
            bool ok = true;
            var raise = new List<int[]>();
            var raiseTo = new List<float>();
            foreach (int side in new[] { -1, 1 })
            {
                int sx = n.X + dz * side, sz = n.Z + dx * side;   // perpendicular to the way
                if (!In(sx, sz)) { ok = false; break; }
                CellData sc = Cells[sx, sz];
                if (HasWall(n.X, n.Z, sx, sz) && !indoors) { ok = false; break; }   // a wall's top could be reached
                if (indoors || sc.Kind == Kind.Void) continue;   // the ceiling or a drop: nothing to climb
                // Everything you could stand on beside it must be either within normal reach already, or still out
                // of reach from the top of the vault wall. Buildings that are neither get raised.
                float outOfReach = c.Floor + VaultHeight + Climb + 0.2f;
                bool Fine(float surface) => surface <= c.Floor + Climb || surface >= outOfReach;
                if (sc.Kind == Kind.Filler)
                {
                    if (!Fine(sc.Top)) { raise.Add(new[] { sx, sz }); raiseTo.Add(outOfReach); }
                    continue;
                }
                if (!Fine(sc.Floor) || (sc.Kind == Kind.Covered && !Fine(sc.Top))) { ok = false; break; }
            }
            if (!ok) continue;
            for (int i = 0; i < raise.Count; i++)   // buildings beside it go up so its top is no step up onto them
                Cells[raise[i][0], raise[i][1]].Top = Cells[raise[i][0], raise[i][1]].Floor = raiseTo[i];

            bool acrossX = dx == 0;   // walking along z: the wall runs along x
            Vaults.Add(new Slab
            {
                Name = "Vault_Wall", X = Centre(n.X), Y = c.Floor + VaultHeight / 2f, Z = Centre(n.Z),
                SX = acrossX ? Cell : VaultDepth, SY = VaultHeight, SZ = acrossX ? VaultDepth : Cell,
            });
            placed.Add(new[] { n.X, n.Z });
        }
    }

    bool HasWall(int ax, int az, int bx, int bz)
    {
        foreach (Wall w in Walls)
            if (EdgeKey(w.AX, w.AZ, w.BX, w.BZ) == EdgeKey(ax, az, bx, bz)) return true;
        return false;
    }
    // ------------------------------------------------------------------ low beams to slide under

    const float LowBeamBottom = 1.25f, LowBeamThickness = 0.45f, LowBeamDepth = 0.35f;   // crouching is 1.1 m tall

    /// <summary>
    /// Fallen beams and ducts across straight, level stretches of the way, wall to wall at 1.25 m, so you slide or
    /// crouch under them. Too thin to stand on. Never right next to a vault wall, three cells from pipes and each other, away from
    /// stairs, planks, slopes and doorways. Added to <see cref="Pipes"/> so the wall-to-wall check covers them.
    /// </summary>
    void AddLowBeams()
    {
        var busy = new HashSet<int>();
        void Mark(float x0, float z0, float x1, float z1, int margin)
        {
            for (int x = (int)Math.Floor(Math.Min(x0, x1) / Cell) - margin; x <= (int)Math.Floor(Math.Max(x0, x1) / Cell) + margin; x++)
                for (int z = (int)Math.Floor(Math.Min(z0, z1) / Cell) - margin; z <= (int)Math.Floor(Math.Max(z0, z1) / Cell) + margin; z++)
                    busy.Add(x * 100 + z);
        }
        foreach (Ramp r in Ramps.Concat(PlankRamps).Concat(Slides)) Mark(r.X0, r.Z0, r.X1, r.Z1, 1);
        foreach (Slab b in Landings.Concat(Planks)) Mark(b.X - b.SX / 2, b.Z - b.SZ / 2, b.X + b.SX / 2, b.Z + b.SZ / 2, 1);

        var taken = new List<int[]>();   // pipes and beams: keep 3 cells apart
        foreach (Slab o in Pipes) taken.Add(new[] { (int)Math.Floor(o.X / Cell), (int)Math.Floor(o.Z / Cell) });
        var vaultCells = new List<int[]>();   // vault walls: not right next to one
        foreach (Slab o in Vaults) vaultCells.Add(new[] { (int)Math.Floor(o.X / Cell), (int)Math.Floor(o.Z / Cell) });

        var order = new List<Node>(bestNext.Keys);
        order.Sort((a, b) => movesLeft[b].CompareTo(movesLeft[a]));
        int count = 0;
        foreach (Node n in order)
        {
            if (!Walkable(n) || n.X == StartX && n.Z == StartZ || busy.Contains(n.X * 100 + n.Z)) continue;
            Node next = bestNext[n];
            if (!Walkable(next) || Math.Abs(next.X - n.X) + Math.Abs(next.Z - n.Z) != 1) continue;
            int dx = next.X - n.X, dz = next.Z - n.Z, bx = n.X - dx, bz = n.Z - dz;
            if (!In(bx, bz) || !Walkable(new Node(bx, bz, false))) continue;
            CellData c = Cells[n.X, n.Z];
            if (Math.Abs(Cells[next.X, next.Z].Floor - c.Floor) > 0.1f || Math.Abs(Cells[bx, bz].Floor - c.Floor) > 0.1f) continue;
            if (HasWall(n.X, n.Z, next.X, next.Z) || HasWall(n.X, n.Z, bx, bz)) continue;
            float top = c.Floor + LowBeamBottom + LowBeamThickness;
            if (c.Kind == Kind.Covered && c.Ceil < top + 0.3f) continue;
            bool clear = true;
            foreach (int[] t in taken) if (Math.Abs(t[0] - n.X) + Math.Abs(t[1] - n.Z) < 3) clear = false;
            foreach (int[] t in vaultCells) if (Math.Abs(t[0] - n.X) + Math.Abs(t[1] - n.Z) < 2) clear = false;
            if (!clear) continue;
            // wall to wall: both ends against something solid at least as high as the beam
            if (!PipeEndSupported(n.X, n.Z, n.X + dz, n.Z + dx, top) || !PipeEndSupported(n.X, n.Z, n.X - dz, n.Z - dx, top)) continue;

            bool acrossX = dx == 0;   // walking along z: the beam runs along x
            Pipes.Add(new Slab
            {
                Name = $"Low_Beam_x{++count}", X = Centre(n.X), Y = c.Floor + LowBeamBottom + LowBeamThickness / 2f, Z = Centre(n.Z),
                SX = acrossX ? Cell : LowBeamDepth, SY = LowBeamThickness, SZ = acrossX ? LowBeamDepth : Cell,
            });
            taken.Add(new[] { n.X, n.Z });
        }
    }
    // ------------------------------------------------------------------ route checker

    struct Node : IEquatable<Node>
    {
        public readonly int X, Z;
        public readonly bool Roof;   // on top of a covered cell rather than inside it
        public Node(int x, int z, bool roof) { X = x; Z = z; Roof = roof; }
        public bool Equals(Node o) => X == o.X && Z == o.Z && Roof == o.Roof;
        public override bool Equals(object o) => o is Node n && Equals(n);
        public override int GetHashCode() => (X * 31 + Z) * 2 + (Roof ? 1 : 0);
        public override string ToString() => Roof ? $"({X},{Z}) roof" : $"({X},{Z})";
    }

    static readonly int[] DX = { 1, -1, 0, 0 }, DZ = { 0, 0, 1, -1 };

    static readonly Node Outside = new Node(-1, -1, false);   // the ground round the city (walk back to the entrance)

    float Surface(Node n) => n.Equals(Outside) ? 0f : n.Roof ? Cells[n.X, n.Z].Top : Cells[n.X, n.Z].Floor;

    static float Obstruction(CellData c) => c.Kind == Kind.Void ? -100f : c.Kind == Kind.Covered ? c.Top : c.Floor;

    Node NodeAt(int x, int z, float y) =>
        new Node(x, z, Cells[x, z].Kind == Kind.Covered && Math.Abs(Cells[x, z].Top - y) < 0.6f);

    HashSet<long> solidWalls;

    static long EdgeKey(int ax, int az, int bx, int bz)
    {
        long a = ax * 100 + az, b = bx * 100 + bz;
        return Math.Min(a, b) * 100000 + Math.Max(a, b);
    }

    /// <summary>Is there a solid wall or junk pile between the cells at this height (below its top)?</summary>
    bool WallBetween(int ax, int az, int bx, int bz, float level)
    {
        if (solidWalls == null)
        {
            solidWalls = new HashSet<long>();
            foreach (Wall w in Walls)
                if (!w.Door) solidWalls.Add(EdgeKey(w.AX, w.AZ, w.BX, w.BZ));
        }
        return solidWalls.Contains(EdgeKey(ax, az, bx, bz)) && level < WallTop(Cells[ax, az], Cells[bx, bz]) - 0.1f;
    }

    /// <summary>Top of a wall between two cells: the higher ceiling if either is indoors (so a taller room is closed all
    /// the way up), otherwise 4.2 m above the higher floor (more than you can climb).</summary>
    public static float WallTop(CellData a, CellData b)
    {
        float floor = 0f, ceil = float.PositiveInfinity;
        foreach (CellData c in new[] { a, b })
        {
            if (c.Kind != Kind.Covered && c.Kind != Kind.Route) continue;
            floor = Math.Max(floor, c.Floor);
            if (c.Kind == Kind.Covered) ceil = float.IsInfinity(ceil) ? c.Ceil : Math.Max(ceil, c.Ceil);
        }
        return float.IsInfinity(ceil) ? floor + 4.2f : ceil;
    }

    /// <summary>Where the player can get to from n in one move: walking, stepping/mantling up, dropping, or jumping a
    /// gap of one or two cells. Ceilings must leave room to stand; solid walls block. Deliberately generous so it
    /// catches shortcuts.</summary>
    IEnumerable<Node> Moves(Node n)
    {
        if (n.Equals(Outside)) { yield return new Node(StartX, StartZ, false); yield break; }
        CellData a = Cells[n.X, n.Z];
        float level = Surface(n);
        bool inside = a.Kind == Kind.Covered && !n.Roof;
        float myCeil = inside ? a.Ceil : NoCeiling;
        for (int d = 0; d < 4; d++)
        {
            int bx = n.X + DX[d], bz = n.Z + DZ[d];
            if (!In(bx, bz)) { if (!inside) yield return Outside; continue; }
            CellData b = Cells[bx, bz];
            bool walled = WallBetween(n.X, n.Z, bx, bz, level);
            if (b.Kind != Kind.Void && !walled)
            {
                // into b at its floor (inside it, if it's covered)
                float s = b.Floor;
                if (s - level <= Climb && myCeil - s >= Body && b.Ceil - level >= Body && b.Ceil - s >= Body)
                    yield return new Node(bx, bz, false);
                // onto the roof of a covered cell
                if (!inside && b.Kind == Kind.Covered && b.Top - level <= Climb) yield return new Node(bx, bz, true);
            }

            if (inside || walled || Obstruction(b) >= level) continue;
            for (int step = 2; step <= 3; step++)   // jumping over lower cells
            {
                int cx = n.X + DX[d] * step, cz = n.Z + DZ[d] * step;
                if (!In(cx, cz)) break;
                CellData c = Cells[cx, cz];
                if (c.Kind != Kind.Void)
                {
                    float target = c.Kind == Kind.Covered ? c.Top : c.Floor;
                    bool reach = step == 2 ? target - level <= 3f : target <= level - 1f;
                    if (reach) yield return new Node(cx, cz, c.Kind == Kind.Covered);
                }
                if (Obstruction(c) >= level) break;
            }
        }
    }

    Dictionary<Node, List<Node>> LinkMap(Link skip)
    {
        var links = new Dictionary<Node, List<Node>>();
        foreach (Link l in Links)
        {
            if (l == skip) continue;
            Node a = NodeAt(l.AX, l.AZ, l.AY), b = NodeAt(l.BX, l.BZ, l.BY);
            if (!links.TryGetValue(a, out var la)) links[a] = la = new List<Node>();
            if (!links.TryGetValue(b, out var lb)) links[b] = lb = new List<Node>();
            la.Add(b);
            lb.Add(a);
        }
        return links;
    }

    List<Node> Next(Node n, Dictionary<Node, List<Node>> links)
    {
        var next = new List<Node>(Moves(n));
        if (links.TryGetValue(n, out var extra)) next.AddRange(extra);
        return next;
    }

    Dictionary<Node, Node> Search(Link skip, bool stopAtGoal = false)
    {
        var links = LinkMap(skip);
        var goal = new Node(GoalX, GoalZ, false);
        var start = new Node(StartX, StartZ, false);
        var parent = new Dictionary<Node, Node> { [start] = start };
        var queue = new Queue<Node>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            Node n = queue.Dequeue();
            if (stopAtGoal && n.Equals(goal)) continue;
            foreach (Node m in Next(n, links))
                if (!parent.ContainsKey(m)) { parent[m] = n; queue.Enqueue(m); }
        }
        return parent;
    }

    // ------------------------------------------------------------------ way-finding: lanterns, progress, views of the goal

    public readonly List<Marker> Lanterns = new List<Marker>();   // glowing lanterns where a stair or plank starts
    /// <summary>How far along the route each walkable cell is: 0 at the start, 1 at the goal (by moves left).</summary>
    public readonly float[,] Progress = new float[W, D];
    /// <summary>Walkable cells from which the goal's beam can be seen.</summary>
    public readonly bool[,] SeesGoal = new bool[W, D];
    /// <summary>Notes from opening up views of the goal (what was changed, and where it couldn't be done).</summary>
    public readonly List<string> ViewNotes = new List<string>();

    public const float BeamBottom = 52f, BeamTop = 140f;   // the light beam above the final tower's mast
    const float EyeHeight = 1.6f;
    public const int MaxStepsWithoutView = 4;              // aim: see the goal at least this often (cells)
    public const int WorstStepsWithoutView = 6;            // and never go longer than this without seeing it

    readonly Dictionary<Node, Node> bestNext = new Dictionary<Node, Node>();   // one move closer to the goal
    readonly Dictionary<Node, int> movesLeft = new Dictionary<Node, int>();

    bool Walkable(Node n) => !n.Roof && !n.Equals(Outside) && (Cells[n.X, n.Z].Kind == Kind.Route || Cells[n.X, n.Z].Kind == Kind.Covered);

    /// <summary>
    /// Works out the way to the goal from everywhere on the route (the neighbouring spot one move closer), the
    /// progress value of each cell, and lanterns where the way takes a stair or plank.
    /// </summary>
    public void ComputeGuides()
    {
        Lanterns.Clear();
        bestNext.Clear();
        movesLeft.Clear();
        Array.Clear(Progress, 0, Progress.Length);
        var links = LinkMap(null);

        // Everything reachable from the start, and the moves out of each.
        var start = new Node(StartX, StartZ, false);
        var moves = new Dictionary<Node, List<Node>>();
        var back = new Dictionary<Node, List<Node>>();
        var queue = new Queue<Node>();
        queue.Enqueue(start);
        moves[start] = null;
        while (queue.Count > 0)
        {
            Node n = queue.Dequeue();
            List<Node> next = Next(n, links);
            moves[n] = next;
            foreach (Node m in next)
            {
                if (!back.TryGetValue(m, out var b)) back[m] = b = new List<Node>();
                b.Add(n);
                if (!moves.ContainsKey(m)) { moves[m] = null; queue.Enqueue(m); }
            }
        }

        // Moves left to the goal, walking the moves backwards from it.
        var goal = new Node(GoalX, GoalZ, false);
        movesLeft[goal] = 0;
        queue.Enqueue(goal);
        while (queue.Count > 0)
        {
            Node m = queue.Dequeue();
            if (!back.TryGetValue(m, out var from)) continue;
            foreach (Node n in from)
                if (moves.ContainsKey(n) && !movesLeft.ContainsKey(n)) { movesLeft[n] = movesLeft[m] + 1; queue.Enqueue(n); }
        }
        if (!movesLeft.ContainsKey(start)) return;
        float total = movesLeft[start];

        foreach (var kv in moves)
        {
            Node n = kv.Key;
            if (!movesLeft.TryGetValue(n, out int d) || d == 0 || kv.Value == null) continue;
            foreach (Node m in kv.Value)
                if (movesLeft.TryGetValue(m, out int dm) && dm == d - 1) { bestNext[n] = m; break; }
            if (Walkable(n))
                Progress[n.X, n.Z] = Math.Max(Progress[n.X, n.Z], Math.Min(1f, 1f - d / total));
        }
        Progress[GoalX, GoalZ] = 1f;

        foreach (var kv in bestNext)
        {
            Node n = kv.Key, m = kv.Value;
            if (!Walkable(n) || new List<Node>(Moves(n)).Contains(m)) continue;   // only stair and plank moves
            float ceil = Cells[n.X, n.Z].Kind == Kind.Covered ? Cells[n.X, n.Z].Ceil : NoCeiling;
            Lanterns.Add(new Marker { Name = "Lantern", X = Centre(n.X), Y = Math.Min(Surface(n) + 3.2f, ceil - 0.3f), Z = Centre(n.Z) });
        }
    }

    // ---- lines of sight to the goal's beam

    bool SolidAt(int x, int z, float y)
    {
        CellData c = Cells[x, z];
        switch (c.Kind)
        {
            case Kind.Filler: return y < c.Top;
            case Kind.Route: return y < c.Floor;
            case Kind.Covered: return y < c.Floor || (y > c.Ceil && y < c.Top);
            default: return false;
        }
    }

    /// <summary>Walks the line from eye to target; returns false if it hits anything. If blockers is given, buildings
    /// in the way are collected (cell -> lowest height the line passes through it) instead, and only route geometry
    /// counts as a hit.</summary>
    bool LineClear(float ex, float ey, float ez, float tx, float ty, float tz, Dictionary<int, float> blockers)
    {
        float dx = tx - ex, dy = ty - ey, dz = tz - ez;
        float len = (float)Math.Sqrt(dx * dx + dz * dz);
        int steps = Math.Max(2, (int)(len / 0.25f));
        for (int i = 1; i < steps; i++)
        {
            float t = i / (float)steps;
            float px = ex + dx * t, py = ey + dy * t, pz = ez + dz * t;
            int cx = (int)Math.Floor(px / Cell), cz = (int)Math.Floor(pz / Cell);
            if (!In(cx, cz) || !SolidAt(cx, cz, py)) continue;
            CellData hit = Cells[cx, cz];
            bool canLower = hit.Kind == Kind.Filler || (hit.Kind == Kind.Covered && !hit.Summit && py > hit.Ceil);
            if (blockers == null || !canLower) return false;
            int key = cx * 100 + cz;
            blockers[key] = blockers.TryGetValue(key, out float low) ? Math.Min(low, py) : py;
        }
        return true;
    }

    IEnumerable<float[]> Eyes(int x, int z)
    {
        float y = Cells[x, z].Floor + EyeHeight, cx = Centre(x), cz = Centre(z);
        yield return new[] { cx, y, cz };
        foreach (float o in new[] { -1.2f, 1.2f })
        {
            yield return new[] { cx + o, y, cz };
            yield return new[] { cx, y, cz + o };
        }
    }

    bool CanSeeGoal(int x, int z)
    {
        foreach (float[] e in Eyes(x, z))
            for (float ty = BeamTop; ty >= BeamBottom; ty -= 8f)
                if (LineClear(e[0], e[1], e[2], Mast.X, ty, Mast.Z, null)) return true;
        return false;
    }

    void UpdateViews()
    {
        for (int x = 0; x < W; x++)
            for (int z = 0; z < D; z++)
                SeesGoal[x, z] = (Cells[x, z].Kind == Kind.Route || Cells[x, z].Kind == Kind.Covered) && CanSeeGoal(x, z);
    }

    /// <summary>Lowest a building may be: 3.6 m above any floor next to it (more than you can climb). Jumps from
    /// further away and drops from above are left to <see cref="Validate"/>, which every change is checked with.</summary>
    float MinBuildingHeight(int x, int z)
    {
        float need = 3f;
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                int nx = x + dx, nz = z + dz;
                if (!In(nx, nz)) continue;
                CellData n = Cells[nx, nz];
                need = Math.Max(need, SlideTop(nx, nz) + 3.6f);
                if (n.Kind == Kind.Filler || n.Kind == Kind.Void) continue;
                need = Math.Max(need, n.Floor + 3.6f);
            }
        foreach (Slab v in Vaults)   // buildings next to a vault wall stay out of reach from its top
            if (Math.Abs(Math.Floor(v.X / Cell) - x) <= 1 && Math.Abs(Math.Floor(v.Z / Cell) - z) <= 1)
                need = Math.Max(need, v.Y + v.SY / 2f + Climb + 0.2f);
        return need;
    }

    /// <summary>Tries to open a view of the beam from cell (x, z) by lowering the buildings in the way (never below
    /// their climb-proof height). Picks the line that needs the least lowering.</summary>
    bool OpenView(int x, int z)
    {
        Dictionary<int, float> bestCut = null;
        float bestCost = float.MaxValue;
        foreach (float[] e in Eyes(x, z))
            for (float ty = BeamTop; ty >= BeamBottom; ty -= 8f)
            {
                var blockers = new Dictionary<int, float>();
                if (!LineClear(e[0], e[1], e[2], Mast.X, ty, Mast.Z, blockers)) continue;
                float cost = 0f;
                bool ok = true;
                var cut = new Dictionary<int, float>();
                foreach (var b in blockers)
                {
                    int bx = b.Key / 100, bz = b.Key % 100;
                    CellData c = Cells[bx, bz];
                    float newTop = (float)Math.Floor((b.Value - 0.3f) * 2f) / 2f;
                    // buildings stay climb-proof; roofs over rooms and alleys stay at least 1 m thick
                    float lowest = c.Kind == Kind.Filler ? MinBuildingHeight(bx, bz) : c.Ceil + 1f;
                    if (newTop < lowest) { ok = false; break; }
                    cut[b.Key] = newTop;
                    cost += c.Top - newTop;
                }
                if (ok && cost < bestCost) { bestCost = cost; bestCut = cut; }
            }
        if (bestCut == null) return false;

        var saved = (CellData[,])Cells.Clone();
        foreach (var c in bestCut)
        {
            int bx = c.Key / 100, bz = c.Key % 100;
            if (Cells[bx, bz].Kind == Kind.Filler) Cells[bx, bz].Floor = c.Value;
            Cells[bx, bz].Top = c.Value;
        }
        if (Validate().Contains("FAIL"))   // a lower building or roof mustn't become a shortcut
        {
            Array.Copy(saved, Cells, saved.Length);
            return false;
        }
        if (bestCut.Count > 0) ViewNotes.Add($"lowered {bestCut.Count} building(s) or roof(s) so ({x},{z}) sees the goal");
        return true;
    }

    /// <summary>Last resort for a stretch of rooms: take the ceiling off one (a collapsed roof open to the sky), then
    /// try to open a view from it. Undone if it opens a shortcut or still doesn't give a view.</summary>
    bool CollapseCeiling(int x, int z)
    {
        CellData before = Cells[x, z];
        if (before.Kind != Kind.Covered || before.Summit) return false;
        var saved = (CellData[,])Cells.Clone();
        int notes = ViewNotes.Count;
        Cells[x, z] = new CellData { Kind = Kind.Route, Floor = before.Floor, Ceil = NoCeiling, Top = before.Floor };
        if (OpenView(x, z) && !Validate().Contains("FAIL"))
        {
            ViewNotes.Add($"collapsed the ceiling at ({x},{z}) to see the goal");
            return true;
        }
        Array.Copy(saved, Cells, saved.Length);
        ViewNotes.RemoveRange(notes, ViewNotes.Count - notes);
        return false;
    }

    /// <summary>
    /// Makes sure that from anywhere on the route, following the way to the goal, you see the goal at least every
    /// MaxStepsWithoutView cells: where a stretch is longer, open a view near its far end.
    /// </summary>
    public void OpenSightlines()
    {
        ViewNotes.Clear();
        UpdateViews();
        var order = new List<Node>(bestNext.Keys);
        order.Sort((a, b) => movesLeft[b].CompareTo(movesLeft[a]));   // from the start towards the goal
        foreach (Node n in order)
        {
            if (!Walkable(n)) continue;
            List<Node> stretch = BlindStretch(n);
            if (stretch.Count <= MaxStepsWithoutView) continue;

            bool fixedIt = false;
            for (int i = Math.Min(MaxStepsWithoutView, stretch.Count - 1); i >= 1 && !fixedIt; i--)
            {
                Node c = stretch[i];
                if (Cells[c.X, c.Z].Kind == Kind.Route) fixedIt = OpenView(c.X, c.Z);
            }
            for (int i = Math.Min(MaxStepsWithoutView, stretch.Count - 1); i >= 1 && !fixedIt; i--)
            {
                Node c = stretch[i];
                fixedIt = CollapseCeiling(c.X, c.Z);
            }
            if (fixedIt) UpdateViews();
        }
    }

    /// <summary>The walkable cells from n along the way to the goal up to the first one that sees it.</summary>
    List<Node> BlindStretch(Node n)
    {
        var stretch = new List<Node>();
        for (Node c = n; ; )
        {
            if (Walkable(c))
            {
                if (SeesGoal[c.X, c.Z]) break;
                stretch.Add(c);
            }
            if (!bestNext.TryGetValue(c, out Node next) || stretch.Count > 40) break;
            c = next;
        }
        return stretch;
    }

    /// <summary>What blocks the view from (x, z) that can't be lowered (first hit along each line), for tuning.</summary>
    public string WhyBlind(int x, int z)
    {
        var hits = new SortedDictionary<string, int>();
        foreach (float[] e in Eyes(x, z))
            for (float ty = BeamTop; ty >= BeamBottom; ty -= 8f)
            {
                float dx = Mast.X - e[0], dy = ty - e[1], dz = Mast.Z - e[2];
                float len = (float)Math.Sqrt(dx * dx + dz * dz);
                int steps = Math.Max(2, (int)(len / 0.25f));
                for (int i = 1; i < steps; i++)
                {
                    float t = i / (float)steps, px = e[0] + dx * t, py = e[1] + dy * t, pz = e[2] + dz * t;
                    int cx = (int)Math.Floor(px / Cell), cz = (int)Math.Floor(pz / Cell);
                    if (!In(cx, cz) || !SolidAt(cx, cz, py)) continue;
                    CellData hit = Cells[cx, cz];
                    if (hit.Kind == Kind.Filler && py >= MinBuildingHeight(cx, cz)) continue;
                    if (hit.Kind == Kind.Covered && !hit.Summit && py > hit.Ceil + 1f) continue;
                    string key = $"({cx},{cz}) {hit.Kind} at {py:0}m";
                    hits[key] = hits.TryGetValue(key, out int c) ? c + 1 : 1;
                    break;
                }
            }
        return string.Join("; ", hits.Keys);
    }

    /// <summary>Longest stretch of route without a view of the goal, following the way to the goal from anywhere.</summary>
    public string ViewReport()
    {
        int worst = 0;
        Node worstFrom = default;
        foreach (Node n in bestNext.Keys)
        {
            if (!Walkable(n)) continue;
            int len = BlindStretch(n).Count;
            if (len > worst) { worst = len; worstFrom = n; }
        }
        int seeing = 0, walkable = 0;
        for (int x = 0; x < W; x++)
            for (int z = 0; z < D; z++)
                if (Cells[x, z].Kind == Kind.Route || Cells[x, z].Kind == Kind.Covered) { walkable++; if (SeesGoal[x, z]) seeing++; }
        var sb = new StringBuilder();
        sb.AppendLine($"{(worst > WorstStepsWithoutView ? "WARN" : "OK")}: the goal is visible from {seeing} of {walkable} route cells; " +
                      $"longest stretch without a view is {worst} cells (from {worstFrom}).");
        foreach (string note in ViewNotes) sb.AppendLine("  " + note);
        return sb.ToString();
    }

    /// <summary>Checks the summit can be reached; that it can't be reached without the required stairs and still can
    /// without each optional one (alternate paths); and that every route cell can be reached. Lines starting with
    /// FAIL are problems.</summary>
    public string Validate()
    {
        var sb = new StringBuilder();
        var goal = new Node(GoalX, GoalZ, false);
        Dictionary<Node, Node> parent = Search(null);
        if (!parent.ContainsKey(goal)) sb.AppendLine("FAIL: the summit can't be reached.");
        else
        {
            var path = new List<Node>();
            for (Node n = goal; ; n = parent[n]) { path.Add(n); if (parent[n].Equals(n)) break; }
            path.Reverse();
            sb.Append($"OK: summit reachable; shortest route {path.Count} cells: ");
            sb.AppendLine(string.Join(" ", path.ConvertAll(p => $"{p}@{Surface(p)}")));
        }

        foreach (Link l in Links)
        {
            bool reachable = Search(l).ContainsKey(goal);
            if (l.Required) sb.AppendLine(reachable ? $"FAIL: the summit can be reached without the {l.Name}."
                                                    : $"OK: the {l.Name} can't be bypassed.");
            else sb.AppendLine(reachable ? $"OK: there's another way round the {l.Name}."
                                         : $"NOTE: the {l.Name} is the only way through.");
        }

        for (int x = 0; x < W; x++)
            for (int z = 0; z < D; z++)
                if ((Cells[x, z].Kind == Kind.Route || Cells[x, z].Kind == Kind.Covered) && !parent.ContainsKey(new Node(x, z, false)))
                    sb.AppendLine($"FAIL: route cell ({x},{z}) can't be reached.");

        // Pipes must run wall to wall: both ends against a building, a solid wall or a higher floor, not an opening.
        foreach (Slab pipe in Pipes)
        {
            int px = (int)Math.Floor(pipe.X / Cell), pz = (int)Math.Floor(pipe.Z / Cell);
            bool alongX = pipe.SX > pipe.SZ;
            float pipeTop = pipe.Y + pipe.SY / 2f;
            foreach (int side in new[] { -1, 1 })
            {
                int nx = px + (alongX ? side : 0), nz = pz + (alongX ? 0 : side);
                if (!PipeEndSupported(px, pz, nx, nz, pipeTop))
                    sb.AppendLine($"FAIL: {pipe.Name} at ({px},{pz}) ends in an opening towards ({nx},{nz}).");
            }
        }

        foreach (Node n in Stuck(Search(null, true).Keys)) sb.AppendLine($"FAIL: you can get stuck at {n} (no way on to the goal).");
        return sb.ToString();
    }

    bool PipeEndSupported(int x, int z, int nx, int nz, float pipeTop)
    {
        if (!In(nx, nz)) return false;
        foreach (Wall w in Walls)
            if (!w.Door && EdgeKey(w.AX, w.AZ, w.BX, w.BZ) == EdgeKey(x, z, nx, nz)) return true;
        CellData n = Cells[nx, nz];
        return n.Kind == Kind.Filler ? n.Top >= pipeTop : n.Kind != Kind.Void && n.Floor >= pipeTop;
    }

    /// <summary>Spots you can get to but can't get from to the goal (e.g. a low roof you dropped onto).</summary>
    List<Node> Stuck(IEnumerable<Node> reachable)
    {
        var links = LinkMap(null);
        var back = new Dictionary<Node, List<Node>>();
        foreach (Node n in reachable)
            foreach (Node m in Next(n, links))
            {
                if (!back.TryGetValue(m, out var b)) back[m] = b = new List<Node>();
                b.Add(n);
            }
        var goal = new Node(GoalX, GoalZ, false);
        var canFinish = new HashSet<Node> { goal };
        var queue = new Queue<Node>();
        queue.Enqueue(goal);
        while (queue.Count > 0)
            if (back.TryGetValue(queue.Dequeue(), out var from))
                foreach (Node n in from)
                    if (canFinish.Add(n)) queue.Enqueue(n);
        var stuck = new List<Node>();
        foreach (Node n in reachable)
            if (!canFinish.Contains(n)) stuck.Add(n);
        return stuck;
    }
    /// <summary>Top-down map of walkable heights (north at the top): * open route, c indoors, plain numbers are
    /// buildings, __ drops.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        for (int z = D - 1; z >= 0; z--)
        {
            sb.Append($"{z,2} ");
            for (int x = 0; x < W; x++)
            {
                CellData c = Cells[x, z];
                sb.Append(c.Kind == Kind.Filler ? $" {(int)c.Top,2}" : c.Kind == Kind.Void ? " __" :
                          c.Kind == Kind.Covered ? $"c{(int)c.Floor,2}" : $"*{(int)c.Floor,2}");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
