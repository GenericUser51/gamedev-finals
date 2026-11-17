using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds the walled-city greybox from <see cref="KowloonLayout"/>: a dense block of buildings with alleys, light
/// wells, corridors, a canyon and a summit tower; the goal is the highest point. Layout only, no props yet.
///
/// Menu: Tools > Kowloon Stage > Build Stage In Open Scene (re-run to rebuild, Ctrl+Z undoes).
/// Built at (300, 0, 0) so it doesn't overlap the movement playground or the hotel.
/// </summary>
public static class KowloonStageBuilder
{
    const string RootName = "KowloonStage";
    const string MaterialFolder = "Assets/Showcase/Greybox";
    static readonly Vector3 LevelOrigin = new Vector3(300f, 0f, 0f);
    const float C = KowloonLayout.Cell;

    static Material groundMat, blockMat, stairMat, plankMat, hazardMat, vaultMat, slideMat, beamMat, lanternMat, beaconMat;
    static Material[] walkMats;

    [MenuItem("Tools/Kowloon Stage/Build Stage In Open Scene")]
    static void Build()
    {
        GameObject existing = GameObject.Find(RootName);
        if (existing != null)
        {
            if (!EditorUtility.DisplayDialog("Rebuild Stage?",
                    $"'{RootName}' already exists in this scene. Replace it with a fresh copy?", "Rebuild", "Cancel"))
                return;
            Undo.DestroyObjectImmediate(existing);
        }

        // The old spa hotel stage has been retired; clear any copy of it still in the scene.
        GameObject hotel = GameObject.Find("LiminalStage");
        if (hotel != null)
        {
            Undo.DestroyObjectImmediate(hotel);
            Debug.Log("Removed the old hotel stage (LiminalStage) from the scene.");
        }

        KowloonLayout k = KowloonLayout.Create();
        LogReport(k);
        CreateMaterials();

        var rootGo = new GameObject(RootName);
        Undo.RegisterCreatedObjectUndo(rootGo, "Build Walled City Greybox");
        rootGo.transform.position = LevelOrigin;
        Transform root = rootGo.transform;

        var spawn = new GameObject(k.Spawn.Name).transform;
        spawn.SetParent(root, false);
        spawn.localPosition = new Vector3(k.Spawn.X, k.Spawn.Y, k.Spawn.Z);
        spawn.localRotation = Quaternion.Euler(0f, k.Spawn.Yaw, 0f);

        float sizeX = KowloonLayout.W * C, sizeZ = KowloonLayout.D * C;
        Box(root, "Ground", new Vector3(sizeX * 0.5f, -0.5f, sizeZ * 0.5f), new Vector3(sizeX + 60f, 1f, sizeZ + 60f), groundMat);

        Transform blocks = Group(root, "Buildings");
        Transform route = Group(root, "Route");
        Transform kill = Group(root, "KillZones");
        for (int x = 0; x < KowloonLayout.W; x++)
            for (int z = 0; z < KowloonLayout.D; z++)
                BuildCell(k.Cells[x, z], x, z, k.Progress[x, z], blocks, route, kill, spawn);

        Transform stairs = Group(root, "Stairs");
        foreach (KowloonLayout.Ramp r in k.Ramps)
            Ramp(stairs, r.Name, new Vector3(r.X0, r.Y0, r.Z0), new Vector3(r.X1, r.Y1, r.Z1), r.Width, stairMat, 0.3f);
        foreach (KowloonLayout.Slab s in k.Landings) SlabBox(stairs, s, stairMat, true);
        Transform slides = Group(root, "Slides");   // slopes to slide down
        foreach (KowloonLayout.Ramp r in k.Slides)
            Ramp(slides, r.Name, new Vector3(r.X0, r.Y0, r.Z0), new Vector3(r.X1, r.Y1, r.Z1), r.Width, slideMat, 0.3f);
        Transform planks = Group(root, "Planks");
        foreach (KowloonLayout.Slab s in k.Planks) SlabBox(planks, s, plankMat, true);
        foreach (KowloonLayout.Ramp r in k.PlankRamps)
            Ramp(planks, r.Name, new Vector3(r.X0, r.Y0, r.Z0), new Vector3(r.X1, r.Y1, r.Z1), r.Width, plankMat, 0.2f);
        Transform walls = Group(root, "Interior_Walls");
        foreach (KowloonLayout.Wall w in k.Walls) BuildWall(walls, k, w);
        Transform pipes = Group(root, "Pipes_And_Low_Beams");   // red: slide or crouch under
        foreach (KowloonLayout.Slab s in k.Pipes) SlabBox(pipes, s, hazardMat, true);
        Transform vaults = Group(root, "Vault_Walls");
        foreach (KowloonLayout.Slab s in k.Vaults) SlabBox(vaults, s, vaultMat, true);

        // The mast on the final tower marks the goal from anywhere in the city, with a red beacon on top.
        KowloonLayout.Marker mast = k.Mast;
        Box(root, "Antenna_Mast", new Vector3(mast.X, mast.Y + 8f, mast.Z), new Vector3(0.3f, 16f, 0.3f), beaconMat, false);
        Box(root, "Antenna_Bar", new Vector3(mast.X, mast.Y + 13f, mast.Z), new Vector3(3f, 0.15f, 0.15f), hazardMat, false);
        Box(root, "Antenna_Beacon", new Vector3(mast.X, mast.Y + 16.3f, mast.Z), new Vector3(0.7f, 0.7f, 0.7f), beaconMat, false);
        PointLight(root, new Vector3(mast.X, mast.Y + 16.3f, mast.Z), 40f, 4f, new Color(1f, 0.2f, 0.15f));
        // A beam of light from the beacon high into the sky: the layout keeps it in view every few rooms.
        float beamBottom = mast.Y + 16.7f;
        Box(root, "Goal_Beam", new Vector3(mast.X, (beamBottom + KowloonLayout.BeamTop) * 0.5f, mast.Z),
            new Vector3(0.8f, KowloonLayout.BeamTop - beamBottom, 0.8f), beamMat, false);

        Transform lights = Group(root, "Lights");
        foreach (KowloonLayout.Marker m in k.Lights) PointLight(lights, new Vector3(m.X, m.Y, m.Z), 9f, 1f, Color.white);

        // Lanterns where a stair or plank starts. The floor colour also warms from blue to orange the closer you are
        // to the top.
        Transform guides = Group(root, "Guides");
        foreach (KowloonLayout.Marker l in k.Lanterns)
        {
            Box(guides, "Lantern", new Vector3(l.X, l.Y, l.Z), new Vector3(0.45f, 0.6f, 0.45f), lanternMat, false);
            PointLight(guides, new Vector3(l.X, l.Y - 0.5f, l.Z), 6f, 1.5f, new Color(0.3f, 1f, 0.9f));
        }

        Transform cps = Group(root, "Checkpoints");
        foreach (KowloonLayout.Marker m in k.Checkpoints)
            CheckpointAt(cps, m.Name, new Vector3(m.X, m.Y, m.Z), new Vector3(C, 2.5f, C), m.Yaw);

        KillVolume(kill, "KillZone_Below", new Vector3(sizeX * 0.5f, -20f, sizeZ * 0.5f), new Vector3(400f, 4f, 400f), spawn);

        MovePlayerToStart(spawn);
        EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
        Selection.activeGameObject = rootGo;
        Debug.Log("Walled city greybox built at (300, 0, 0). Press Play to try it; save the scene (Ctrl+S) to keep it.", rootGo);
    }

    [MenuItem("Tools/Kowloon Stage/Check Route")]
    static void CheckRoute()
    {
        KowloonLayout k = KowloonLayout.Create();
        LogReport(k);
        Debug.Log("Walled city heights (north at the top, * route, c corridor, ## buildings, __ drop):\n" + k.Describe());
    }

    [MenuItem("Tools/Kowloon Stage/Move Player To Stage Start")]
    static void MovePlayerMenu()
    {
        GameObject root = GameObject.Find(RootName);
        Transform spawn = root != null ? root.transform.Find("PlayerSpawn") : null;
        if (spawn == null)
        {
            EditorUtility.DisplayDialog("Kowloon Stage", "Build the stage first.", "OK");
            return;
        }
        MovePlayerToStart(spawn);
    }

    [MenuItem("Tools/Kowloon Stage/Remove Stage From Open Scene")]
    static void Remove()
    {
        GameObject existing = GameObject.Find(RootName);
        if (existing == null) return;
        Undo.DestroyObjectImmediate(existing);
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
    }

    static void LogReport(KowloonLayout k)
    {
        string report = k.Validate() + "\n" + k.ViewReport();
        if (report.Contains("FAIL") || report.Contains("WARN")) Debug.LogWarning("Walled city route check found problems:\n" + report);
        else Debug.Log("Walled city route check passed:\n" + report);
    }

    // ------------------------------------------------------------------ cells

    static void BuildCell(KowloonLayout.CellData c, int x, int z, float progress, Transform blocks, Transform route,
                          Transform kill, Transform spawn)
    {
        float cx = KowloonLayout.Centre(x), cz = KowloonLayout.Centre(z);
        string id = $"{x}_{z}";
        switch (c.Kind)
        {
            case KowloonLayout.Kind.Filler:
                Column(blocks, $"Block_{id}", cx, cz, 0f, c.Top);
                break;

            case KowloonLayout.Kind.Route:
                Column(route, $"Roof_{id}", cx, cz, 0f, c.Floor);
                Walkway(route, id, cx, c.Floor, cz, progress);
                break;

            case KowloonLayout.Kind.Covered:   // passage at Floor with building above it
                Column(route, $"Base_{id}", cx, cz, 0f, c.Floor);
                Column(blocks, $"Over_{id}", cx, cz, c.Ceil, c.Top);
                Walkway(route, id, cx, c.Floor, cz, progress);
                break;

            case KowloonLayout.Kind.Void:      // a drop to the bottom of the canyon
                Box(kill, $"Drop_Floor_{id}", new Vector3(cx, 0.02f, cz), new Vector3(C, 0.04f, C), hazardMat, false);
                KillVolume(kill, $"KillZone_{id}", new Vector3(cx, 1f, cz), new Vector3(C, 2f, C), spawn);
                break;
        }
    }

    static void Column(Transform p, string name, float cx, float cz, float bottom, float top)
    {
        if (top - bottom < 0.01f) return;
        Box(p, name, new Vector3(cx, (bottom + top) * 0.5f, cz), new Vector3(C, top - bottom, C), blockMat);
    }

    /// <summary>Coloured cap on walkable cells so the route reads at a glance (visual only; the column is the floor).
    /// The colour band shows progress: blue near the start, warming to orange near the top.</summary>
    static void Walkway(Transform p, string id, float cx, float y, float cz, float progress)
    {
        int band = Mathf.Clamp(Mathf.FloorToInt(progress * walkMats.Length), 0, walkMats.Length - 1);
        Box(p, $"Walk_{id}", new Vector3(cx, y - 0.04f, cz), new Vector3(C, 0.1f, C), walkMats[band], false);
    }

    /// <summary>
    /// Wall on the edge between two cells, from the indoor floor up to the higher ceiling (so a taller room next to a
    /// lower one is closed all the way up). A doorway leaves a 2.4 m gap in the middle, open all the way up.
    /// </summary>
    static void BuildWall(Transform p, KowloonLayout k, KowloonLayout.Wall w)
    {
        KowloonLayout.CellData a = k.Cells[w.AX, w.AZ], b = k.Cells[w.BX, w.BZ];
        float bottom = 0f;
        foreach (KowloonLayout.CellData c in new[] { a, b })
            if (c.Kind == KowloonLayout.Kind.Covered || c.Kind == KowloonLayout.Kind.Route) bottom = Mathf.Max(bottom, c.Floor);
        float top = KowloonLayout.WallTop(a, b);   // same height the route checker assumes

        bool alongZ = w.AX != w.BX;   // cells side by side in x share a wall running along z
        float edge = alongZ ? Mathf.Max(w.AX, w.BX) * C : Mathf.Max(w.AZ, w.BZ) * C;
        float start = (alongZ ? w.AZ : w.AX) * C;
        const float thick = 0.2f, gap = 2.4f;   // wide, full-height doorways: nothing to catch on
        string name = w.Door ? "Doorway" : "Wall";

        void Piece(float from, float to, float y0, float y1)
        {
            if (to - from < 0.01f || y1 - y0 < 0.01f) return;
            float mid = (from + to) * 0.5f, len = to - from;
            Vector3 centre = alongZ ? new Vector3(edge, (y0 + y1) * 0.5f, mid) : new Vector3(mid, (y0 + y1) * 0.5f, edge);
            Vector3 size = alongZ ? new Vector3(thick, y1 - y0, len) : new Vector3(len, y1 - y0, thick);
            Box(p, name, centre, size, blockMat);
        }

        if (!w.Door) { Piece(start, start + C, bottom, top); return; }
        float g0 = start + (C - gap) * 0.5f, g1 = g0 + gap;
        Piece(start, g0, bottom, top);
        Piece(g1, start + C, bottom, top);
    }

    static void SlabBox(Transform p, KowloonLayout.Slab s, Material m, bool collider) =>
        Box(p, s.Name, new Vector3(s.X, s.Y, s.Z), new Vector3(s.SX, s.SY, s.SZ), m, collider);

    // ------------------------------------------------------------------ gameplay volumes and player

    static void CheckpointAt(Transform p, string name, Vector3 floorPos, Vector3 size, float yaw)
    {
        var go = new GameObject(name);
        go.transform.SetParent(p, false);
        go.transform.localPosition = floorPos;   // the respawn point (feet)
        go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
        var box = go.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.center = new Vector3(0f, size.y * 0.5f, 0f);
        box.size = size;
        go.AddComponent<Checkpoint>();
    }

    static void KillVolume(Transform p, string name, Vector3 center, Vector3 size, Transform spawn)
    {
        var go = new GameObject(name);
        go.transform.SetParent(p, false);
        go.transform.localPosition = center;
        var box = go.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = size;
        go.AddComponent<KillZone>().SetDefaultSpawn(spawn);
    }

    static void MovePlayerToStart(Transform spawn)
    {
        CharacterController player = Object.FindAnyObjectByType<CharacterController>();
        if (player == null)
        {
            Debug.LogWarning("Kowloon Stage: no CharacterController player in the scene; move your player to the " +
                             "stage's PlayerSpawn yourself.");
            return;
        }
        Undo.RecordObject(player.transform, "Move Player To Stage");
        player.transform.SetPositionAndRotation(spawn.position, spawn.rotation);

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
        if (target.GetComponent<T>() == null) Undo.AddComponent<T>(target);
    }

    // ------------------------------------------------------------------ building blocks

    static Transform Group(Transform parent, string name)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        return t;
    }

    static GameObject Box(Transform p, string name, Vector3 center, Vector3 size, Material m, bool collider = true)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(p, false);
        go.transform.localPosition = center;
        go.transform.localScale = size;
        if (m != null) go.GetComponent<Renderer>().sharedMaterial = m;
        if (!collider) Object.DestroyImmediate(go.GetComponent<Collider>());
        return go;
    }

    /// <summary>Sloped slab whose top surface runs exactly from topStart to topEnd.</summary>
    static GameObject Ramp(Transform p, string name, Vector3 topStart, Vector3 topEnd, float width, Material m, float thickness)
    {
        Vector3 along = topEnd - topStart;
        Quaternion rotation = Quaternion.LookRotation(along.normalized, Vector3.up);
        Vector3 surfaceNormal = rotation * Vector3.up;
        GameObject go = Box(p, name, (topStart + topEnd) * 0.5f - surfaceNormal * (thickness * 0.5f),
                            new Vector3(width, thickness, along.magnitude), m);
        go.transform.localRotation = rotation;
        return go;
    }

    static void PointLight(Transform p, Vector3 pos, float range, float intensity, Color color)
    {
        var go = new GameObject("Light");
        go.transform.SetParent(p, false);
        go.transform.localPosition = pos;
        var l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.range = range;
        l.intensity = intensity;
        l.color = color;
        l.shadows = LightShadows.None;
    }

    // ------------------------------------------------------------------ greybox materials

    static void CreateMaterials()
    {
        groundMat = Mat("GB_Ground", new Color(0.42f, 0.42f, 0.42f));
        blockMat = Mat("GB_CityBlock", new Color(0.56f, 0.56f, 0.58f));
        stairMat = Mat("GB_Stairs", new Color(0.45f, 0.55f, 0.7f));
        plankMat = Mat("GB_Furniture", new Color(0.66f, 0.62f, 0.56f)); // bridges and crates
        hazardMat = Mat("GB_Hazard", new Color(0.85f, 0.3f, 0.25f));   // pipes and low beams, drops
        vaultMat = Mat("GB_Vault", new Color(0.92f, 0.86f, 0.55f));    // low walls to vault over
        slideMat = Mat("GB_Slide", new Color(0.55f, 0.78f, 0.82f));    // slopes to slide down

        // Route floor, by progress: cool near the start, warm near the top.
        Color far = new Color(0.62f, 0.72f, 0.86f), near = new Color(0.98f, 0.72f, 0.38f);
        walkMats = new Material[5];
        for (int i = 0; i < walkMats.Length; i++)
            walkMats[i] = Mat($"GB_Walkway_{i}", Color.Lerp(far, near, i / (walkMats.Length - 1f)));

        beamMat = Glow("GB_Goal_Beam", new Color(1f, 0.45f, 0.3f), 2f);        // the goal's light beam
        lanternMat = Glow("GB_Guide_Lantern", new Color(0.3f, 1f, 0.9f), 3f);   // stairs and planks start here
        beaconMat = Glow("GB_Goal_Beacon", new Color(1f, 0.15f, 0.1f), 4f);     // the goal's mast
    }

    /// <summary>A material that glows (emission), so guides stand out in dark corridors.</summary>
    static Material Glow(string name, Color color, float strength)
    {
        Material m = Mat(name, color);
        if (m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", color * strength);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        EditorUtility.SetDirty(m);
        return m;
    }

    static Material Mat(string name, Color color)
    {
        if (!AssetDatabase.IsValidFolder("Assets/Showcase")) AssetDatabase.CreateFolder("Assets", "Showcase");
        if (!AssetDatabase.IsValidFolder(MaterialFolder)) AssetDatabase.CreateFolder("Assets/Showcase", "Greybox");
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
}
