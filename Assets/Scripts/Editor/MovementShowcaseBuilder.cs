using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds an open movement playground in the open scene: a walled 120 x 120 m arena with stations you can
/// explore in any order:
///   pick-up-and-throw crates and balls, parkour blocks, mantle walls, crouch tunnel, slide bars,
///   a tall plateau (walk-up ramp + mantle tower)
///   with a slide-jump ramp off it, a wall-run corridor, a wall-run gap, a moving platform between towers,
///   and a ramp park for momentum. The boundary walls are wall-runnable too.
///
/// Menu: Tools > Movement Showcase > Build Level In Open Scene. Running it again rebuilds it (Ctrl+Z undoes).
/// Distances are tuned for the Starter Assets FirstPersonController (walk 6, sprint 9, jump 1.2, gravity -16.5)
/// plus the FirstPerson* add-ons (crouch/slide, momentum, mantle, wall run).
/// </summary>
public static class MovementShowcaseBuilder
{
    const string RootName = "MovementShowcase";
    const string MaterialFolder = "Assets/Showcase/Materials";

    // Built off to the side so it doesn't overlap the existing Plane and Cube at the origin.
    static readonly Vector3 LevelOrigin = new Vector3(110f, 0f, 0f);

    const float ArenaHalfSize = 60f;
    const float BoundaryHeight = 8f;

    // Plateau and slide-jump. With gravity -16.5 and 2x fall gravity, a jump off the ramp lip is airborne
    // ~0.82 s: sprinting down the ramp (~14.5 m/s) reaches ~11.9 m; a slide (~20 m/s) reaches ~16.4 m.
    const float PlateauHeight = 10f;
    const float RampRun = 16f;
    const float RampAngle = 22f;
    const float SlideJumpGap = 14f;
    const float LandingDrop = 2f;

    // Wall-run gap: too far to jump (~6 m), a sprinting wall run along the side wall covers it.
    const float WallRunGap = 12f;
    const float WallRunPlatformHeight = 3f;

    [MenuItem("Tools/Movement Showcase/Build Level In Open Scene")]
    static void Build()
    {
        GameObject existing = GameObject.Find(RootName);
        if (existing != null)
        {
            if (!EditorUtility.DisplayDialog("Rebuild Movement Showcase?",
                    $"'{RootName}' already exists in this scene. Replace it with a fresh copy?", "Rebuild", "Cancel"))
                return;
            Undo.DestroyObjectImmediate(existing);
        }

        var floor = Mat("Showcase_Floor", new Color(0.55f, 0.57f, 0.6f));
        var start = Mat("Showcase_StartFinish", new Color(0.3f, 0.75f, 0.4f));
        var jump = Mat("Showcase_Jump", new Color(0.95f, 0.55f, 0.2f));
        var climb = Mat("Showcase_Climb", new Color(0.3f, 0.55f, 0.95f));
        var obstacle = Mat("Showcase_Obstacle", new Color(0.85f, 0.25f, 0.3f));
        var slide = Mat("Showcase_Slide", new Color(0.95f, 0.8f, 0.2f));
        var moving = Mat("Showcase_Moving", new Color(0.75f, 0.35f, 0.9f));
        var wallRun = Mat("Showcase_WallRun", new Color(0.25f, 0.8f, 0.85f));

        var root = new GameObject(RootName);
        Undo.RegisterCreatedObjectUndo(root, "Build Movement Showcase");
        root.transform.position = LevelOrigin;
        Transform t = root.transform;

        // ---- Arena ----
        Box(t, "Ground", new Vector3(0f, -0.5f, 0f), new Vector3(ArenaHalfSize * 2f + 10f, 1f, ArenaHalfSize * 2f + 10f), floor);
        float wallLength = ArenaHalfSize * 2f + 1f;
        Box(t, "Boundary_North", new Vector3(0f, BoundaryHeight * 0.5f, ArenaHalfSize), new Vector3(wallLength, BoundaryHeight, 1f), wallRun);
        Box(t, "Boundary_South", new Vector3(0f, BoundaryHeight * 0.5f, -ArenaHalfSize), new Vector3(wallLength, BoundaryHeight, 1f), wallRun);
        Box(t, "Boundary_East", new Vector3(ArenaHalfSize, BoundaryHeight * 0.5f, 0f), new Vector3(1f, BoundaryHeight, wallLength), wallRun);
        Box(t, "Boundary_West", new Vector3(-ArenaHalfSize, BoundaryHeight * 0.5f, 0f), new Vector3(1f, BoundaryHeight, wallLength), wallRun);

        // ---- Spawn ----
        var spawn = new GameObject("PlayerSpawn").transform;
        spawn.SetParent(t, false);
        spawn.localPosition = new Vector3(0f, 0.1f, -50f);
        Box(t, "Spawn_Pad", new Vector3(0f, 0.05f, -50f), new Vector3(6f, 0.1f, 6f), start);
        Label(t, "MOVEMENT PLAYGROUND\n" +
                 "WASD move  -  Shift sprint  -  Space jump  -  C slide / crouch  -  E pick up  -  Click throw\n" +
                 "Explore anywhere. Cyan walls are wall-runnable (including the arena walls).",
              new Vector3(0f, 4.5f, -43f), 0.045f);
        Label(t, "OPEN GROUND: BUNNYHOP\njump again right as you land", new Vector3(16f, 3f, -48f));

        // ---- Pick up & throw (south, just east of spawn) ----
        Label(t, "PICK UP & THROW\nE pick up / drop  -  left click throw\nknock down the stack, clear the bottles!",
              new Vector3(17f, 4.5f, -41f));
        // Everything here is an instance of a reusable prefab in Assets/Showcase/Prefabs.
        ShowcasePrefabs prefabs = EnsurePrefabs();
        const float crate = 0.8f, gap = 0.82f;
        for (int row = 0; row < 3; row++)            // 3-2-1 pyramid
            for (int i = 0; i < 3 - row; i++)
                Place(t, prefabs.Crate, $"Stack_Crate_{row}_{i}",
                      new Vector3(17f + (i - (2 - row) * 0.5f) * gap, crate * 0.5f + row * crate, -32f));
        Place(t, prefabs.Crate, "Crate_1", new Vector3(13f, crate * 0.5f, -36f));
        Place(t, prefabs.Crate, "Crate_2", new Vector3(14.5f, crate * 0.5f, -37.5f));
        Place(t, prefabs.Crate, "Crate_3", new Vector3(20f, crate * 0.5f, -37f));
        Place(t, prefabs.HeavyCrate, "Heavy_Crate", new Vector3(21.5f, 0.7f, -33.5f));
        for (int i = 0; i < 4; i++)
            Place(t, prefabs.BouncyBall, $"Ball_{i + 1}", new Vector3(12.5f + i * 1.2f, 0.3f, -33f));

        // Table with bottles to grab, throw, or knock off.
        Vector3 tableCenter = new Vector3(17f, 0f, -38.8f);
        Place(t, prefabs.Table, "Bottle_Table", tableCenter);
        for (int i = 0; i < 6; i++)
        {
            float x = tableCenter.x - 1.05f + i * 0.42f;
            float z = tableCenter.z + (i % 2 == 0 ? -0.15f : 0.15f); // staggered
            Place(t, i % 2 == 0 ? prefabs.BottleGreen : prefabs.BottleBrown, $"Bottle_{i + 1}",
                  new Vector3(x, TableHeight, z));
        }

        // ---- Parkour blocks (south-west) ----
        Label(t, "PARKOUR BLOCKS", new Vector3(-33f, 5.5f, -47f));
        Block(t, "Parkour_1", -36f, -44f, 2.5f, 2.5f, 0.8f, jump);
        Block(t, "Parkour_2", -32f, -40f, 2.5f, 2.5f, 1.6f, jump);
        Block(t, "Parkour_3", -28f, -36f, 2.5f, 2.5f, 2.4f, jump);
        Block(t, "Parkour_4", -32f, -31f, 2.5f, 2.5f, 3.2f, jump);
        Block(t, "Parkour_5", -37f, -27f, 2.5f, 2.5f, 2.4f, jump);
        Block(t, "Parkour_6", -41f, -32f, 2.5f, 2.5f, 1.6f, jump);

        // ---- Mantle walls (south, centre-left) ----
        Label(t, "MANTLE\njump at a ledge, hold W", new Vector3(-8f, 4.5f, -34f));
        Block(t, "Mantle_1.5m", -14f, -30f, 4f, 3f, 1.5f, climb);
        Block(t, "Mantle_2.0m", -8f, -30f, 4f, 3f, 2.0f, climb);
        Block(t, "Mantle_2.3m", -2f, -30f, 4f, 3f, 2.3f, climb);

        // ---- Crouch tunnel and slide bars (south-east) ----
        Label(t, "CROUCH TUNNEL\nhold C", new Vector3(30f, 4f, -45f));
        const float tunnelClearance = 1.4f; // standing is 2 m, crouched 1.1 m
        Box(t, "Tunnel_Wall_L", new Vector3(28.25f, tunnelClearance * 0.5f, -37f), new Vector3(0.5f, tunnelClearance, 10f), obstacle);
        Box(t, "Tunnel_Wall_R", new Vector3(31.75f, tunnelClearance * 0.5f, -37f), new Vector3(0.5f, tunnelClearance, 10f), obstacle);
        Box(t, "Tunnel_Roof", new Vector3(30f, tunnelClearance + 0.4f, -37f), new Vector3(4f, 0.8f, 10f), obstacle);

        Label(t, "SLIDE UNDER\nsprint, then C", new Vector3(43f, 4f, -45f));
        const float barClearance = 1.3f;
        for (int i = 0; i < 3; i++)
            Box(t, $"Slide_Bar_{i + 1}", new Vector3(43f, barClearance + 0.3f, -40f + i * 6f), new Vector3(6f, 0.6f, 0.6f), obstacle);

        // ---- Plateau: walk-up ramp, mantle tower, slide-jump (north, centre) ----
        Block(t, "Plateau", 0f, 12.5f, 14f, 15f, PlateauHeight, climb);
        Label(t, "UPHILL RAMP", new Vector3(-26f, 3f, 9f));
        Ramp(t, "Uphill_Ramp", new Vector3(-30f, 0f, 12.5f), new Vector3(-7f, PlateauHeight, 12.5f), 5f, floor);

        Label(t, "MANTLE TOWER", new Vector3(18f, 4f, 9f));
        for (int i = 0; i < 4; i++) // 2, 4, 6, 8 m steps up to the 10 m plateau
            Block(t, $"Mantle_Tower_{i + 1}", 17.5f - i * 3f, 12.5f, 3f, 3f, 2f * (i + 1), climb);

        float rampStartZ = 20f;
        float lipZ = rampStartZ + RampRun;
        float lipY = PlateauHeight - RampRun * Mathf.Tan(RampAngle * Mathf.Deg2Rad);
        Label(t, $"SLIDE-JUMP  ({SlideJumpGap:0.#} m gap)\nslide down, jump at the edge",
              new Vector3(0f, PlateauHeight + 3.5f, 17f));
        Ramp(t, "Slide_Ramp", new Vector3(0f, PlateauHeight, rampStartZ), new Vector3(0f, lipY, lipZ), 6f, slide);
        float landingTop = lipY - LandingDrop;
        float landingStart = lipZ + SlideJumpGap;
        Block(t, "Slide_Jump_Landing", 0f, (landingStart + ArenaHalfSize - 1f) * 0.5f, 10f,
              ArenaHalfSize - 1f - landingStart, landingTop, jump);

        // ---- Wall-run corridor (east) ----
        Label(t, "WALL RUN CORRIDOR\nsprint, jump beside a wall, hold W\nSpace to jump to the other wall",
              new Vector3(39f, 7.5f, -13f));
        Box(t, "Corridor_Wall_W", new Vector3(36f, 3f, 7.5f), new Vector3(1f, 6f, 35f), wallRun);
        Box(t, "Corridor_Wall_E", new Vector3(42f, 3f, 7.5f), new Vector3(1f, 6f, 35f), wallRun);

        // ---- Wall-run gap (west) ----
        Label(t, $"WALL RUN GAP  ({WallRunGap:0.#} m)\nrun along the wall to cross",
              new Vector3(-40f, WallRunPlatformHeight + 4f, -21f));
        Ramp(t, "WallRun_Gap_Ramp", new Vector3(-40f, 0f, -30f), new Vector3(-40f, WallRunPlatformHeight, -18f), 6f, floor);
        float platformAEnd = -8f;
        Block(t, "WallRun_Gap_Start", -40f, -13f, 6f, 10f, WallRunPlatformHeight, jump);
        Block(t, "WallRun_Gap_End", -40f, platformAEnd + WallRunGap + 5f, 6f, 10f, WallRunPlatformHeight, jump);
        Box(t, "WallRun_Gap_Wall", new Vector3(-43.5f, 4f, -1f), new Vector3(1f, 8f, 40f), wallRun);

        // ---- Moving platform between towers (north-west) ----
        Label(t, "MOVING PLATFORM\nmantle up, ride across", new Vector3(-30f, 7.5f, 35f));
        const float towerHeight = 4f;
        Block(t, "Tower_West", -45f, 40f, 6f, 6f, towerHeight, climb);
        Block(t, "Tower_East", -15f, 40f, 6f, 6f, towerHeight, climb);
        Block(t, "Tower_West_Step", -45f, 35.5f, 4f, 3f, 2f, climb);
        Block(t, "Tower_East_Step", -15f, 35.5f, 4f, 3f, 2f, climb);
        // Gap between the towers runs x -42..-18; the 4 m platform shuttles flush from one to the other.
        MovingPlatformAt(t, new Vector3(-40f, towerHeight, 40f), 4f, new Vector3(20f, 0f, 0f), moving);

        // ---- Ramp park (north-east) ----
        Label(t, "RAMP PARK\n15  -  25  -  35 degrees", new Vector3(38f, 6f, 31f));
        float[] angles = { 15f, 25f, 35f };
        for (int i = 0; i < angles.Length; i++)
        {
            float x = 30f + i * 8f;
            float height = 10f * Mathf.Tan(angles[i] * Mathf.Deg2Rad);
            Ramp(t, $"Ramp_{angles[i]:0}deg", new Vector3(x, 0f, 35f), new Vector3(x, height, 45f), 5f, slide);
        }

        // Falling out of the world sends you back to the spawn pad.
        GameObject kill = Box(t, "KillZone", new Vector3(0f, -20f, 0f), new Vector3(300f, 4f, 300f), null);
        Object.DestroyImmediate(kill.GetComponent<MeshRenderer>());
        Object.DestroyImmediate(kill.GetComponent<MeshFilter>());
        kill.GetComponent<BoxCollider>().isTrigger = true;
        kill.AddComponent<KillZone>().SetDefaultSpawn(spawn);

        MovePlayerToStart(spawn);

        EditorSceneManager.MarkSceneDirty(root.scene);
        Selection.activeGameObject = root;
        Debug.Log("Movement playground built. Press Play to try it; save the scene (Ctrl+S) to keep it.", root);
    }

    // ---- Prefabs ----

    const string PrefabFolder = "Assets/Showcase/Prefabs";

    class ShowcasePrefabs
    {
        public GameObject Crate, HeavyCrate, BouncyBall, BottleGreen, BottleBrown, Table;
    }

    [MenuItem("Tools/Movement Showcase/Create Object Prefabs")]
    static void CreatePrefabsMenu()
    {
        ShowcasePrefabs prefabs = EnsurePrefabs();
        EditorGUIUtility.PingObject(prefabs.Crate);
        Debug.Log($"Movement Showcase: object prefabs are in {PrefabFolder}. Drag them into any scene. " +
                  "Existing prefabs are never overwritten; delete one to regenerate it.");
    }

    /// <summary>
    /// Swaps the playground's plain crates, balls, bottles and table for instances of their prefabs, in place
    /// (same position and rotation), so a playground built before prefabs existed gets them, including the
    /// bottles' held pose. Already-prefab objects and the level geometry are left alone. Ctrl+Z undoes it.
    /// </summary>
    [MenuItem("Tools/Movement Showcase/Replace Playground Objects With Prefabs")]
    static void ReplaceObjectsWithPrefabs()
    {
        if (GameObject.Find(RootName) == null)
        {
            EditorUtility.DisplayDialog("Replace With Prefabs",
                $"No '{RootName}' in the open scene. Build the playground first.", "OK");
            return;
        }
        ReplaceObjectsWithPrefabsInScene();
    }

    /// <summary>Does the replacement; returns how many objects were swapped, or -1 if there's no playground.</summary>
    internal static int ReplaceObjectsWithPrefabsInScene()
    {
        GameObject root = GameObject.Find(RootName);
        if (root == null) return -1;

        ShowcasePrefabs prefabs = EnsurePrefabs();
        Undo.SetCurrentGroupName("Replace Playground Objects With Prefabs");
        int undoGroup = Undo.GetCurrentGroup();

        // Copy the list first: we add and remove children while going through it.
        var children = new System.Collections.Generic.List<Transform>();
        foreach (Transform child in root.transform) children.Add(child);

        int replaced = 0;
        foreach (Transform original in children)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(original.gameObject)) continue; // already a prefab
            GameObject prefab = PrefabFor(original.gameObject, prefabs);
            if (prefab == null) continue; // level geometry, labels, etc.

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
            Undo.RegisterCreatedObjectUndo(instance, "Replace With Prefab");
            instance.name = original.name;
            instance.transform.SetPositionAndRotation(original.position, original.rotation);
            instance.transform.SetSiblingIndex(original.GetSiblingIndex());
            Undo.DestroyObjectImmediate(original.gameObject);
            replaced++;
        }

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(root.scene);
        Debug.Log($"Movement Showcase: replaced {replaced} object(s) with prefab instances from {PrefabFolder}. " +
                  "Save the scene (Ctrl+S) to keep it.", root);
        return replaced;
    }

    /// <summary>Which prefab a plain playground object should become, by its name (and colour, for bottles).</summary>
    static GameObject PrefabFor(GameObject go, ShowcasePrefabs prefabs)
    {
        string name = go.name;
        if (name.StartsWith("Bottle_Table")) return prefabs.Table;
        if (name.StartsWith("Bottle_"))
        {
            Renderer renderer = go.GetComponentInChildren<Renderer>();
            bool brown = renderer != null && renderer.sharedMaterial != null &&
                         renderer.sharedMaterial.name.Contains("Brown");
            return brown ? prefabs.BottleBrown : prefabs.BottleGreen;
        }
        if (name == "Heavy_Crate") return prefabs.HeavyCrate;
        if (name.StartsWith("Crate_") || name.StartsWith("Stack_Crate_")) return prefabs.Crate;
        if (name.StartsWith("Ball_")) return prefabs.BouncyBall;
        return null;
    }

    /// <summary>
    /// Loads the reusable object prefabs, creating any that don't exist yet. Existing prefabs are left alone,
    /// so edits made to them survive rebuilding the playground.
    /// </summary>
    static ShowcasePrefabs EnsurePrefabs()
    {
        var crateMat = Mat("Showcase_Jump", new Color(0.95f, 0.55f, 0.2f));
        var heavyMat = Mat("Showcase_Obstacle", new Color(0.85f, 0.25f, 0.3f));
        var ballMat = Mat("Showcase_Moving", new Color(0.75f, 0.35f, 0.9f));
        var wood = Mat("Showcase_Wood", new Color(0.45f, 0.3f, 0.18f));
        var greenGlass = Mat("Showcase_Bottle_Green", new Color(0.15f, 0.55f, 0.25f), 0.85f);
        var brownGlass = Mat("Showcase_Bottle_Brown", new Color(0.45f, 0.25f, 0.08f), 0.85f);
        PhysicsMaterial bouncy = BouncyMaterial();

        // Pivots: crates and balls at their centre, bottles and the table at their base.
        return new ShowcasePrefabs
        {
            Crate = GetOrCreatePrefab("Crate", () => PhysicsBox(null, "Crate", Vector3.zero, 0.8f, 4f, crateMat)),
            HeavyCrate = GetOrCreatePrefab("HeavyCrate", () => PhysicsBox(null, "HeavyCrate", Vector3.zero, 1.4f, 20f, heavyMat)),
            BouncyBall = GetOrCreatePrefab("BouncyBall", () => PhysicsBall(null, "BouncyBall", Vector3.zero, 0.6f, 1.5f, ballMat, bouncy)),
            BottleGreen = GetOrCreatePrefab("Bottle_Green", () => Bottle(null, "Bottle_Green", Vector3.zero, greenGlass)),
            BottleBrown = GetOrCreatePrefab("Bottle_Brown", () => Bottle(null, "Bottle_Brown", Vector3.zero, brownGlass)),
            Table = GetOrCreatePrefab("Table", () => Table(null, "Table", Vector3.zero, 2.6f, 1f, TableHeight, wood)),
        };
    }

    const float TableHeight = 0.9f;

    static GameObject GetOrCreatePrefab(string name, System.Func<GameObject> build)
    {
        if (!AssetDatabase.IsValidFolder("Assets/Showcase")) AssetDatabase.CreateFolder("Assets", "Showcase");
        if (!AssetDatabase.IsValidFolder(PrefabFolder)) AssetDatabase.CreateFolder("Assets/Showcase", "Prefabs");

        string path = $"{PrefabFolder}/{name}.prefab";
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (existing != null) return existing;

        GameObject temp = build();
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(temp, path);
        Object.DestroyImmediate(temp);
        return prefab;
    }

    /// <summary>Places an instance of a prefab (still linked to it) under the level.</summary>
    static GameObject Place(Transform parent, GameObject prefab, string name, Vector3 localPosition)
    {
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
        instance.name = name;
        instance.transform.localPosition = localPosition;
        instance.transform.localRotation = Quaternion.identity;
        return instance;
    }

    [MenuItem("Tools/Movement Showcase/Remove Level From Open Scene")]
    static void Remove()
    {
        GameObject existing = GameObject.Find(RootName);
        if (existing == null) return;
        Undo.DestroyObjectImmediate(existing);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
    }

    // ---- Player ----

    static void MovePlayerToStart(Transform spawn)
    {
        CharacterController player = Object.FindAnyObjectByType<CharacterController>();
        if (player == null)
        {
            Debug.LogWarning("Movement Showcase: no CharacterController player found in the scene, " +
                             "move your player to the green spawn pad yourself.");
            return;
        }

        Undo.RecordObject(player.transform, "Move Player To Showcase Start");
        player.transform.SetPositionAndRotation(spawn.position, spawn.rotation);

        // The playground uses every move; add any that a Starter Assets player is missing.
        if (player.GetComponent<StarterAssets.FirstPersonController>() != null)
        {
            AddIfMissing<FirstPersonCrouchSlide>(player.gameObject);
            AddIfMissing<FirstPersonMomentum>(player.gameObject);
            AddIfMissing<FirstPersonMantle>(player.gameObject);
            AddIfMissing<FirstPersonWallRun>(player.gameObject);
            AddIfMissing<FirstPersonCameraTilt>(player.gameObject);
            AddIfMissing<FirstPersonSprintFov>(player.gameObject);
            AddIfMissing<FirstPersonCameraBob>(player.gameObject);
            AddIfMissing<FirstPersonGrab>(player.gameObject);
        }
    }

    static void AddIfMissing<T>(GameObject target) where T : Component
    {
        if (target.GetComponent<T>() != null) return;
        Undo.AddComponent<T>(target);
        Debug.Log($"Movement Showcase: added {typeof(T).Name} to {target.name}", target);
    }

    // ---- Building blocks ----

    static GameObject Box(Transform parent, string name, Vector3 localCenter, Vector3 size, Material material)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localCenter;
        go.transform.localScale = size;
        if (material != null) go.GetComponent<Renderer>().sharedMaterial = material;
        return go;
    }

    /// <summary>Solid block standing on the ground (y = 0) with its top at the given height.</summary>
    static GameObject Block(Transform parent, string name, float x, float z, float sizeX, float sizeZ, float height,
                            Material material)
    {
        return Box(parent, name, new Vector3(x, height * 0.5f, z), new Vector3(sizeX, height, sizeZ), material);
    }

    /// <summary>Sloped slab whose top surface runs exactly from topStart to topEnd.</summary>
    static GameObject Ramp(Transform parent, string name, Vector3 topStart, Vector3 topEnd, float width,
                           Material material, float thickness = 1f)
    {
        Vector3 along = topEnd - topStart;
        Quaternion rotation = Quaternion.LookRotation(along.normalized, Vector3.up);
        Vector3 surfaceNormal = rotation * Vector3.up;
        GameObject go = Box(parent, name, (topStart + topEnd) * 0.5f - surfaceNormal * (thickness * 0.5f),
                            new Vector3(width, thickness, along.magnitude), material);
        go.transform.localRotation = rotation;
        return go;
    }

    static GameObject PhysicsBox(Transform parent, string name, Vector3 localCenter, float size, float mass, Material material)
    {
        GameObject go = Box(parent, name, localCenter, Vector3.one * size, material);
        go.AddComponent<Rigidbody>().mass = mass;
        return go;
    }

    static GameObject PhysicsBall(Transform parent, string name, Vector3 localCenter, float diameter, float mass,
                                  Material material, PhysicsMaterial physicsMaterial)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localCenter;
        go.transform.localScale = Vector3.one * diameter;
        go.GetComponent<Renderer>().sharedMaterial = material;
        go.GetComponent<Collider>().sharedMaterial = physicsMaterial;
        go.AddComponent<Rigidbody>().mass = mass;
        return go;
    }

    /// <summary>Fixed table: a top at the given height on four legs, standing on the ground at center.</summary>
    static GameObject Table(Transform parent, string name, Vector3 center, float length, float depth, float height,
                            Material material)
    {
        var table = new GameObject(name).transform;
        table.SetParent(parent, false);
        table.localPosition = center;

        const float topThickness = 0.08f, leg = 0.08f;
        Box(table, "Top", new Vector3(0f, height - topThickness * 0.5f, 0f),
            new Vector3(length, topThickness, depth), material);
        float legHeight = height - topThickness;
        float lx = length * 0.5f - leg, lz = depth * 0.5f - leg;
        for (int i = 0; i < 4; i++)
        {
            float x = i < 2 ? -lx : lx;
            float z = i % 2 == 0 ? -lz : lz;
            Box(table, $"Leg_{i + 1}", new Vector3(x, legHeight * 0.5f, z), new Vector3(leg, legHeight, leg), material);
        }
        return table.gameObject;
    }

    /// <summary>
    /// Physics bottle standing with its base at localBase: a body and a neck, one Rigidbody on the parent.
    /// Uses convex mesh colliders so the base is flat (the default cylinder collider is a rounded capsule,
    /// which would tip straight over).
    /// </summary>
    static GameObject Bottle(Transform parent, string name, Vector3 localBase, Material material)
    {
        var bottle = new GameObject(name);
        bottle.transform.SetParent(parent, false);
        bottle.transform.localPosition = localBase;

        const float bodyDiameter = 0.12f, bodyHeight = 0.28f, neckDiameter = 0.05f, neckHeight = 0.1f;
        BottlePart(bottle.transform, "Body", new Vector3(0f, bodyHeight * 0.5f, 0f),
                   new Vector3(bodyDiameter, bodyHeight * 0.5f, bodyDiameter), material); // cylinder mesh is 2 tall
        BottlePart(bottle.transform, "Neck", new Vector3(0f, bodyHeight + neckHeight * 0.5f, 0f),
                   new Vector3(neckDiameter, neckHeight * 0.5f, neckDiameter), material);

        var body = bottle.AddComponent<Rigidbody>();
        body.mass = 0.5f;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        // Held upside down by the neck, tipped a little forward and to the side.
        bottle.AddComponent<HeldPose>().SetRotation(new Vector3(160f, 0f, 15f));
        return bottle;
    }

    static void BottlePart(Transform parent, string name, Vector3 localPosition, Vector3 scale, Material material)
    {
        GameObject part = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        part.name = name;
        part.transform.SetParent(parent, false);
        part.transform.localPosition = localPosition;
        part.transform.localScale = scale;
        part.GetComponent<Renderer>().sharedMaterial = material;

        Object.DestroyImmediate(part.GetComponent<Collider>()); // rounded capsule
        var meshCollider = part.AddComponent<MeshCollider>();
        meshCollider.sharedMesh = part.GetComponent<MeshFilter>().sharedMesh;
        meshCollider.convex = true; // required for colliders on a moving Rigidbody
    }

    // Saved as an asset (like the render materials) so the scene keeps it.
    static PhysicsMaterial BouncyMaterial()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Showcase")) AssetDatabase.CreateFolder("Assets", "Showcase");
        if (!AssetDatabase.IsValidFolder(MaterialFolder)) AssetDatabase.CreateFolder("Assets/Showcase", "Materials");

        string path = $"{MaterialFolder}/Showcase_Bouncy.asset";
        var material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
        if (material == null)
        {
            material = new PhysicsMaterial("Showcase_Bouncy");
            AssetDatabase.CreateAsset(material, path);
        }
        material.bounciness = 0.75f;
        material.bounceCombine = PhysicsMaterialCombine.Maximum;
        material.dynamicFriction = 0.4f;
        material.staticFriction = 0.4f;
        EditorUtility.SetDirty(material);
        return material;
    }

    static void MovingPlatformAt(Transform parent, Vector3 localTopCenter, float size, Vector3 travel, Material material)
    {
        var go = new GameObject("Moving_Platform");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localTopCenter;

        var body = go.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;

        // Trigger just above the surface detects who is standing on it.
        var riderZone = go.AddComponent<BoxCollider>();
        riderZone.isTrigger = true;
        riderZone.center = new Vector3(0f, 0.75f, 0f);
        riderZone.size = new Vector3(size - 0.2f, 1.5f, size - 0.2f);

        Box(go.transform, "Surface", new Vector3(0f, -0.25f, 0f), new Vector3(size, 0.5f, size), material);

        go.AddComponent<MovingPlatform>().Configure(travel, 3f, 1f);
    }

    static void Label(Transform parent, string text, Vector3 localPosition, float characterSize = 0.05f)
    {
        var go = new GameObject("Label_" + text.Split('\n')[0]);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPosition;

        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var textMesh = go.AddComponent<TextMesh>();
        textMesh.text = text;
        textMesh.font = font;
        textMesh.fontSize = 100;
        textMesh.characterSize = characterSize;
        textMesh.anchor = TextAnchor.MiddleCenter;
        textMesh.alignment = TextAlignment.Center;
        textMesh.color = Color.white;
        go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
    }

    // Materials are saved as assets so the scene keeps them after it's closed and reopened.
    static Material Mat(string name, Color color, float smoothness = 0.15f)
    {
        if (!AssetDatabase.IsValidFolder("Assets/Showcase")) AssetDatabase.CreateFolder("Assets", "Showcase");
        if (!AssetDatabase.IsValidFolder(MaterialFolder)) AssetDatabase.CreateFolder("Assets/Showcase", "Materials");

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
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
        EditorUtility.SetDirty(material);
        return material;
    }
}
