using System.Collections.Generic;
using UnityEngine;

// TANDEM DRIFT — a low-poly drift CHASE BATTLE (追走 / "tsuiso"), not a solo time-attack.
// One control: STEER (arrows / A,D / hold-drag pointer & touch). Throttle is automatic.
//
// CORE LOOP (the differentiator vs a normal drift-circuit time-attack): a RIVAL pace car runs the
// racing line ahead of you forever. Your job is to ride its bumper — stay inside its proximity ZONE
// AND drift to charge the LOCK meter. The closer you ride + the harder you slide, the faster LOCK
// fills and the more SCORE you bank. Fall out of the zone (drop too far back, or overtake) and LOCK
// drains, the screen reddens — empty = CHASE LOST. Fill LOCK to 100% to clear a ROUND: bonus points,
// the rival gets FASTER, and the bar resets a little lower. Escalating one-more-round tension.
//
// Built entirely in code (CreatePrimitive + a couple procedural meshes) so it renders reliably in
// WebGL with engine-code stripping disabled. NO Rigidbody/colliders: the player is pure
// Transform-driven (hand-integrated arcade drift), the rival is driven along the sampled centerline
// by arc length, and all tests are distance/projection checks. Coexists with Juice & AutoShot.
public class TandemDrift : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        Application.runInBackground = true;
        var go = new GameObject("__TandemDrift");
        go.AddComponent<TandemDrift>();
        DontDestroyOnLoad(go);
    }

    // ---- scene refs ----
    Transform carT, carVisual;     // player root (pos+yaw) + cosmetic child
    Transform rivalT, rivalVisual; // rival root + cosmetic child
    Transform cam; Camera camComp;
    TextMesh hudRound, hudScore, hudBest, hudSpeed, statusText, bannerText, dbg, gapText;
    Transform lockBarBG, lockBarFill, zoneBG, zoneFill; // HUD quads

    // ---- track (sampled closed centerline) ----
    Vector3[] pts; Vector3[] leftN; float[] cum; float[] curv;
    float trackLen; int N;
    const float HALF_W = 6.0f, SOFT_W = 7.6f;

    // ---- player state ----
    enum State { Playing, Over }
    State state = State.Playing;
    Vector3 pos; float heading, velAngle, speed;
    int nearIdx; float steerInput, camYaw, fovPunch;
    float playerS;

    // ---- rival state ----
    float rivalS, rivalSpeed, rivalBase; int rivalIdx;
    float runT;   // seconds since this run began (launch ramp + start grace)

    // ---- chase / lock ----
    float gap;                 // signed forward distance to rival along track (+behind rival, -ahead)
    bool locked, drifting;
    float lockMeter = 70f;     // 0..100, the "health"
    int round = 1;
    float redTint;             // grows as lock drains
    float driftFactor;         // 0..1 current slide intensity (for scoring + smoke)
    float lockedTime;          // seconds continuously locked (combo feel)

    const float ZONE_MAX = 26f;     // locked if 0 <= gap <= ZONE_MAX
    const float ZONE_GOOD = 13f;    // tighter than this rewards more

    // ---- scoring ----
    float scoreF; int score, best;
    float comboFlash, smokeT, sessionT, bannerTimer, statusPulse;

    // ---- decorations ----
    class Cone { public Transform t; public Vector3 p; public bool knocked; }
    readonly List<Cone> cones = new List<Cone>();

    // ---- HUD layout (aspect-adaptive) ----
    float hudScale = 1f, halfH = 2.7f, halfW = 4.6f;
    const float HUD_Z = 6.5f;

    // ---- tuning ----
    const float MAX_SPEED = 34f, GRASS_MAX = 13f, ACCEL = 25f;
    const float TURN_RATE = 145f, DRIFT_DEG = 13f;

    bool attract = true, showDbg;

    // ===================================================================== boot
    void Start()
    {
        foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None)) Destroy(c.gameObject);
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) Destroy(l.gameObject);

        best = PlayerPrefs.GetInt("tandemdrift_best", 0);

        BuildEnvironment();
        BuildTrack();
        BuildCamera();
        BuildCars();
        BuildCones();
        BuildHud();

        ResetRun(true);
        UpdateCamera(0.0001f, true);
    }

    void ResetRun(bool firstBoot)
    {
        state = State.Playing;
        pos = pts[0];
        heading = velAngle = HeadingFromTo(pts[0], pts[1 % N]);
        nearIdx = 0; speed = 23f;     // launch already up to pace so you start matched, not from a stop
        playerS = cum[0];
        rivalBase = 24.0f; rivalSpeed = rivalBase;
        rivalS = 12f;                 // rival starts ~12m ahead, mid-zone
        rivalIdx = 0; runT = 0f;
        round = 1; score = 0; scoreF = 0f; lockMeter = 70f; redTint = 0f;
        locked = drifting = false; driftFactor = 0f; lockedTime = 0f;
        camYaw = heading; fovPunch = 0f; comboFlash = 0f;
        bannerText.text = ""; statusText.text = ""; bannerTimer = 0f;
        foreach (var c in cones) { if (c.t) { c.knocked = false; } }
        SyncCar(); UpdateRivalTransform();
        RefreshHud();
        if (!firstBoot) Banner("CHASE ON!", new Color(0.5f, 1f, 0.8f), 1.2f);
    }

    // ===================================================================== materials / meshes
    static Material Mat(Color c, float metallic = 0f, float smooth = 0.2f, bool emissive = false, float emi = 0.7f)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (emissive && m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", c * emi); }
        return m;
    }

    static GameObject Prim(PrimitiveType pt, Transform parent, Vector3 lpos, Vector3 lscale, Color c, Material shared = null)
    {
        var g = GameObject.CreatePrimitive(pt);
        var col = g.GetComponent<Collider>(); if (col != null) Destroy(col);
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lpos; g.transform.localScale = lscale;
        g.GetComponent<Renderer>().sharedMaterial = shared != null ? shared : Mat(c);
        return g;
    }

    static Mesh _cone;
    static Mesh ConeMesh()
    {
        if (_cone != null) return _cone;
        int seg = 10; var v = new List<Vector3>(); var tri = new List<int>();
        v.Add(new Vector3(0, 1f, 0));
        for (int i = 0; i < seg; i++) { float a = i * Mathf.PI * 2f / seg; v.Add(new Vector3(Mathf.Cos(a) * 0.5f, 0f, Mathf.Sin(a) * 0.5f)); }
        int baseC = v.Count; v.Add(Vector3.zero);
        for (int i = 0; i < seg; i++) { int a = 1 + i, b = 1 + (i + 1) % seg; tri.Add(0); tri.Add(b); tri.Add(a); tri.Add(baseC); tri.Add(a); tri.Add(b); }
        _cone = new Mesh(); _cone.SetVertices(v); _cone.SetTriangles(tri, 0); _cone.RecalculateNormals(); _cone.RecalculateBounds();
        return _cone;
    }

    GameObject MeshObj(Mesh m, Transform parent, Vector3 lpos, Vector3 lscale, Color c, Material shared = null)
    {
        var g = new GameObject("m");
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lpos; g.transform.localScale = lscale;
        g.AddComponent<MeshFilter>().sharedMesh = m;
        g.AddComponent<MeshRenderer>().sharedMaterial = shared != null ? shared : Mat(c);
        return g;
    }

    // ===================================================================== world
    Material asphaltMat, lineMat, grassMat, bodyMat, rivalMat, coneMat, treeMat, trunkMat, tireMat;

    void BuildEnvironment()
    {
        asphaltMat = Mat(new Color(0.15f, 0.16f, 0.20f), 0.05f, 0.35f);
        lineMat    = Mat(new Color(0.95f, 0.95f, 0.98f), 0f, 0.1f);
        grassMat   = Mat(new Color(0.16f, 0.34f, 0.20f), 0f, 0.05f);
        bodyMat    = Mat(new Color(1.0f, 0.27f, 0.18f), 0.3f, 0.7f);             // player: red
        rivalMat   = Mat(new Color(0.25f, 0.85f, 0.95f), 0.35f, 0.75f, true, 0.45f); // rival: glowing teal
        coneMat    = Mat(new Color(1f, 0.55f, 0.08f), 0f, 0.3f, true);
        treeMat    = Mat(new Color(0.15f, 0.46f, 0.30f), 0f, 0.05f);
        trunkMat   = Mat(new Color(0.32f, 0.21f, 0.13f), 0f, 0.05f);
        tireMat    = Mat(new Color(0.06f, 0.06f, 0.07f), 0f, 0.2f);

        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional; sun.color = new Color(1f, 0.96f, 0.88f); sun.intensity = 1.18f;
        sun.transform.rotation = Quaternion.Euler(50f, 28f, 0f); sun.shadows = LightShadows.Soft;

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor     = new Color(0.52f, 0.63f, 0.85f);
        RenderSettings.ambientEquatorColor = new Color(0.48f, 0.53f, 0.58f);
        RenderSettings.ambientGroundColor  = new Color(0.20f, 0.25f, 0.22f);

        RenderSettings.fog = true; RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.60f, 0.70f, 0.85f);
        RenderSettings.fogStartDistance = 130f; RenderSettings.fogEndDistance = 340f;

        var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var gc = g.GetComponent<Collider>(); if (gc != null) Destroy(gc);
        g.name = "Grass";
        g.transform.localScale = new Vector3(900f, 1f, 900f);
        g.transform.position = new Vector3(0, -0.55f, 0);
        g.GetComponent<Renderer>().sharedMaterial = grassMat;
    }

    void BuildTrack()
    {
        float[] var12 = { 0.06f, -0.16f, 0.12f, -0.22f, 0.16f, -0.10f, 0.22f, -0.26f, 0.06f, 0.18f, -0.13f, -0.02f };
        int K = var12.Length; float baseR = 74f;
        var cp = new Vector3[K];
        for (int i = 0; i < K; i++)
        {
            float a = i * Mathf.PI * 2f / K, r = baseR * (1f + var12[i]);
            cp[i] = new Vector3(Mathf.Cos(a) * r * 1.15f, 0f, Mathf.Sin(a) * r * 0.92f);
        }
        const int SEG = 16; N = K * SEG;
        pts = new Vector3[N]; int idx = 0;
        for (int s = 0; s < K; s++)
        {
            Vector3 p0 = cp[(s - 1 + K) % K], p1 = cp[s], p2 = cp[(s + 1) % K], p3 = cp[(s + 2) % K];
            for (int j = 0; j < SEG; j++) { float t = (float)j / SEG; pts[idx++] = CatmullRom(p0, p1, p2, p3, t); }
        }

        leftN = new Vector3[N]; cum = new float[N]; curv = new float[N];
        float acc = 0f;
        for (int i = 0; i < N; i++)
        {
            Vector3 fwd = (pts[(i + 1) % N] - pts[(i - 1 + N) % N]); fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward; fwd.Normalize();
            leftN[i] = new Vector3(-fwd.z, 0f, fwd.x);
            cum[i] = acc; acc += (pts[(i + 1) % N] - pts[i]).magnitude;
        }
        trackLen = acc;
        // per-point curvature (0 straight .. 1 sharp) — used to slow the rival in corners
        for (int i = 0; i < N; i++)
        {
            Vector3 a = (pts[i] - pts[(i - 1 + N) % N]); a.y = 0;
            Vector3 b = (pts[(i + 1) % N] - pts[i]); b.y = 0;
            if (a.sqrMagnitude < 1e-5f || b.sqrMagnitude < 1e-5f) { curv[i] = 0; continue; }
            float ang = Vector3.Angle(a, b);          // deg between consecutive segments
            curv[i] = Mathf.Clamp01(ang / 12f);
        }
        // light smoothing pass
        var sm = new float[N];
        for (int i = 0; i < N; i++) sm[i] = (curv[(i - 1 + N) % N] + curv[i] + curv[(i + 1) % N]) / 3f;
        curv = sm;

        BuildRoadMesh(); BuildStartLine();
    }

    static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    void BuildRoadMesh()
    {
        var rv = new Vector3[N * 2]; var rt = new int[N * 6];
        for (int i = 0; i < N; i++) { rv[i * 2 + 0] = pts[i] + leftN[i] * HALF_W; rv[i * 2 + 1] = pts[i] - leftN[i] * HALF_W; }
        for (int i = 0; i < N; i++)
        {
            int a = i * 2, b = i * 2 + 1, c = ((i + 1) % N) * 2, d = ((i + 1) % N) * 2 + 1, o = i * 6;
            rt[o + 0] = a; rt[o + 1] = c; rt[o + 2] = b; rt[o + 3] = b; rt[o + 4] = c; rt[o + 5] = d;
        }
        var road = new Mesh { name = "road" }; road.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        road.vertices = rv; road.triangles = rt; road.RecalculateNormals(); road.RecalculateBounds();
        var rgo = new GameObject("Road"); rgo.transform.position = new Vector3(0, 0.02f, 0);
        rgo.AddComponent<MeshFilter>().sharedMesh = road; rgo.AddComponent<MeshRenderer>().sharedMaterial = asphaltMat;

        BuildEdgeRibbon(HALF_W - 0.35f, 0.30f, lineMat, 0.03f);
        BuildEdgeRibbon(-(HALF_W - 0.35f), 0.30f, lineMat, 0.03f);
        BuildDashedCenter();
    }

    void BuildEdgeRibbon(float offset, float width, Material mat, float y)
    {
        var rv = new Vector3[N * 2]; var rt = new int[N * 6];
        for (int i = 0; i < N; i++) { rv[i * 2 + 0] = pts[i] + leftN[i] * (offset + width * 0.5f); rv[i * 2 + 1] = pts[i] + leftN[i] * (offset - width * 0.5f); }
        for (int i = 0; i < N; i++)
        {
            int a = i * 2, b = i * 2 + 1, c = ((i + 1) % N) * 2, d = ((i + 1) % N) * 2 + 1, o = i * 6;
            rt[o + 0] = a; rt[o + 1] = c; rt[o + 2] = b; rt[o + 3] = b; rt[o + 4] = c; rt[o + 5] = d;
        }
        var m = new Mesh { name = "edge" }; m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        m.vertices = rv; m.triangles = rt; m.RecalculateNormals(); m.RecalculateBounds();
        var go = new GameObject("Edge"); go.transform.position = new Vector3(0, y, 0);
        go.AddComponent<MeshFilter>().sharedMesh = m; go.AddComponent<MeshRenderer>().sharedMaterial = mat;
    }

    void BuildDashedCenter()
    {
        var dashMat = Mat(new Color(0.85f, 0.78f, 0.3f), 0f, 0.1f);
        for (int i = 0; i < N; i += 6)
        {
            Vector3 fwd = (pts[(i + 1) % N] - pts[i]); fwd.y = 0; fwd.Normalize();
            var q = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var col = q.GetComponent<Collider>(); if (col) Destroy(col);
            q.transform.position = pts[i] + Vector3.up * 0.04f;
            q.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
            q.transform.localScale = new Vector3(0.22f, 0.02f, 2.2f);
            q.GetComponent<Renderer>().sharedMaterial = dashMat;
        }
    }

    void BuildStartLine()
    {
        Vector3 fwd = (pts[1 % N] - pts[0]); fwd.y = 0; fwd.Normalize();
        var black = Mat(new Color(0.05f, 0.05f, 0.05f), 0f, 0.1f);
        int cells = 8;
        for (int c = 0; c < cells; c++)
            for (int row = 0; row < 2; row++)
            {
                float f = (c / (float)cells - 0.5f) * 2f;
                var q = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var col = q.GetComponent<Collider>(); if (col) Destroy(col);
                q.transform.position = pts[0] + leftN[0] * (f * HALF_W) + fwd * (row * 0.9f - 0.45f) + Vector3.up * 0.05f;
                q.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
                q.transform.localScale = new Vector3(HALF_W * 2f / cells, 0.02f, 0.9f);
                q.GetComponent<Renderer>().sharedMaterial = ((c + row) % 2 == 0) ? lineMat : black;
            }
        for (int side = -1; side <= 1; side += 2)
        {
            var p = new GameObject("post");
            p.transform.position = pts[0] + leftN[0] * (side * (HALF_W + 1.2f));
            Prim(PrimitiveType.Cylinder, p.transform, new Vector3(0, 3f, 0), new Vector3(0.4f, 3f, 0.4f), new Color(0.9f, 0.9f, 0.95f));
            Prim(PrimitiveType.Cube, p.transform, new Vector3(side * -1.5f, 6.2f, 0), new Vector3(3.2f, 1f, 0.3f), new Color(0.95f, 0.2f, 0.2f), null);
        }
    }

    void BuildCamera()
    {
        var cgo = new GameObject("MainCamera"); cgo.tag = "MainCamera";
        camComp = cgo.AddComponent<Camera>();
        camComp.clearFlags = CameraClearFlags.SolidColor;
        camComp.backgroundColor = new Color(0.58f, 0.70f, 0.86f);
        camComp.fieldOfView = 60f; camComp.farClipPlane = 600f;
        cgo.AddComponent<AudioListener>(); cam = cgo.transform;
    }

    void BuildCars()
    {
        carT = new GameObject("Car").transform;
        carVisual = new GameObject("CarVisual").transform; carVisual.SetParent(carT, false);
        BuildCarBody(carVisual, bodyMat, false);

        rivalT = new GameObject("Rival").transform;
        rivalVisual = new GameObject("RivalVisual").transform; rivalVisual.SetParent(rivalT, false);
        BuildCarBody(rivalVisual, rivalMat, true);
    }

    // chunky low-poly hot-hatch
    void BuildCarBody(Transform root, Material body, bool rival)
    {
        Prim(PrimitiveType.Cube, root, new Vector3(0, 0.55f, 0.1f), new Vector3(1.9f, 0.6f, 4.0f), default, body);
        Prim(PrimitiveType.Cube, root, new Vector3(0, 1.05f, -0.25f), new Vector3(1.6f, 0.55f, 2.1f), default, body);
        Prim(PrimitiveType.Cube, root, new Vector3(0, 1.07f, 0.85f), new Vector3(1.45f, 0.5f, 0.12f), new Color(0.1f, 0.13f, 0.18f));
        Prim(PrimitiveType.Cube, root, new Vector3(0, 1.07f, -1.32f), new Vector3(1.45f, 0.5f, 0.12f), new Color(0.1f, 0.13f, 0.18f));
        Prim(PrimitiveType.Cube, root, new Vector3(0, 1.15f, -2.0f), new Vector3(1.7f, 0.12f, 0.45f), default, body);
        Prim(PrimitiveType.Cube, root, new Vector3(0.6f, 0.95f, -1.95f), new Vector3(0.12f, 0.35f, 0.2f), default, body);
        Prim(PrimitiveType.Cube, root, new Vector3(-0.6f, 0.95f, -1.95f), new Vector3(0.12f, 0.35f, 0.2f), default, body);
        // lights: player white headlights forward; rival red tail-lights toward you (you chase from behind)
        var hl = Mat(new Color(1f, 0.95f, 0.7f), 0, 0.5f, true);
        Prim(PrimitiveType.Cube, root, new Vector3(0.55f, 0.6f, 2.02f), new Vector3(0.5f, 0.28f, 0.06f), default, hl);
        Prim(PrimitiveType.Cube, root, new Vector3(-0.55f, 0.6f, 2.02f), new Vector3(0.5f, 0.28f, 0.06f), default, hl);
        if (rival)
        {
            var tl = Mat(new Color(1f, 0.15f, 0.12f), 0, 0.5f, true, 1.2f);
            Prim(PrimitiveType.Cube, root, new Vector3(0.55f, 0.62f, -2.04f), new Vector3(0.55f, 0.3f, 0.06f), default, tl);
            Prim(PrimitiveType.Cube, root, new Vector3(-0.55f, 0.62f, -2.04f), new Vector3(0.55f, 0.3f, 0.06f), default, tl);
        }
        float wx = 1.02f, wz = 1.35f, wy = 0.38f;
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                var w = Prim(PrimitiveType.Cylinder, root, new Vector3(sx * wx, wy, sz * wz), new Vector3(0.42f, 0.16f, 0.42f), default, tireMat);
                w.transform.localRotation = Quaternion.Euler(0, 0, 90f);
            }
    }

    void BuildCones()
    {
        for (int i = 0; i < N; i += 11)
        {
            float side = ((i / 11) % 2 == 0) ? 1f : -1f;
            Vector3 p = pts[i] + leftN[i] * side * (HALF_W - 1.1f);
            var go = new GameObject("cone"); go.transform.position = p;
            MeshObj(ConeMesh(), go.transform, Vector3.zero, new Vector3(0.5f, 0.85f, 0.5f), default, coneMat);
            MeshObj(ConeMesh(), go.transform, new Vector3(0, 0.18f, 0), new Vector3(0.7f, 0.12f, 0.7f), Color.white);
            cones.Add(new Cone { t = go.transform, p = p });
        }
        for (int i = 0; i < N; i += 9)
        {
            Vector3 p = pts[i] + leftN[i] * ((i % 18 == 0) ? 1f : -1f) * Random.Range(16f, 34f);
            var go = new GameObject("tree"); go.transform.position = p;
            Prim(PrimitiveType.Cylinder, go.transform, new Vector3(0, 1f, 0), new Vector3(0.5f, 1f, 0.5f), default, trunkMat);
            MeshObj(ConeMesh(), go.transform, new Vector3(0, 1.5f, 0), new Vector3(3.2f, 3.2f, 3.2f), default, treeMat);
            MeshObj(ConeMesh(), go.transform, new Vector3(0, 3.2f, 0), new Vector3(2.4f, 2.6f, 2.4f), default, treeMat);
        }
    }

    // ===================================================================== HUD
    TextMesh MakeText(float size, Color c, TextAnchor anchor)
    {
        var t = new GameObject("T").AddComponent<TextMesh>();
        t.fontSize = 96; t.characterSize = size; t.color = c; t.anchor = anchor;
        t.alignment = TextAlignment.Center;
        t.transform.SetParent(cam, false); t.transform.localRotation = Quaternion.identity;
        return t;
    }

    Transform MakeQuad(Color c, bool emissive)
    {
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        var col = q.GetComponent<Collider>(); if (col) Destroy(col);
        q.transform.SetParent(cam, false);
        var sh = Shader.Find("Sprites/Default"); if (sh == null) sh = Shader.Find("Unlit/Color");
        var m = new Material(sh) { color = c };
        q.GetComponent<MeshRenderer>().sharedMaterial = m;
        return q.transform;
    }

    void BuildHud()
    {
        hudRound  = MakeText(0.075f, Color.white, TextAnchor.UpperLeft);
        hudScore  = MakeText(0.060f, new Color(1f, 0.85f, 0.3f), TextAnchor.UpperLeft);
        hudBest   = MakeText(0.055f, new Color(0.7f, 0.9f, 1f), TextAnchor.UpperRight);
        hudSpeed  = MakeText(0.055f, new Color(0.9f, 0.95f, 1f), TextAnchor.LowerRight);
        gapText   = MakeText(0.050f, new Color(0.6f, 1f, 0.85f), TextAnchor.UpperRight);
        statusText= MakeText(0.10f, new Color(0.5f, 1f, 0.8f), TextAnchor.MiddleCenter);
        bannerText= MakeText(0.13f, Color.white, TextAnchor.MiddleCenter);
        dbg = MakeText(0.040f, new Color(0.6f, 1f, 0.7f), TextAnchor.LowerLeft);
        dbg.gameObject.SetActive(false);
        statusText.text = ""; bannerText.text = "";

        // LOCK meter (top full-width strip) + zone proximity gauge
        lockBarBG   = MakeQuad(new Color(0f, 0f, 0f, 0.45f), false);
        lockBarFill = MakeQuad(new Color(0.4f, 1f, 0.8f, 0.95f), true);
        zoneBG      = MakeQuad(new Color(0f, 0f, 0f, 0.40f), false);
        zoneFill    = MakeQuad(new Color(0.5f, 1f, 0.85f, 0.9f), true);

        AdjustHud(); RefreshHud();
    }

    void AdjustHud()
    {
        if (camComp == null) return;
        float aspect = Mathf.Max(0.3f, camComp.aspect);
        halfH = HUD_Z * Mathf.Tan(camComp.fieldOfView * 0.5f * Mathf.Deg2Rad);
        halfW = halfH * aspect;
        const float REF_HALFW = 6.0f;
        hudScale = Mathf.Clamp(halfW / REF_HALFW, 0.16f, 1.3f);
        float ix = halfW * 0.95f, iy = halfH * 0.93f;

        hudRound.transform.localPosition = new Vector3(-ix, iy, HUD_Z); hudRound.characterSize = 0.075f * hudScale;
        hudScore.transform.localPosition = new Vector3(-ix, iy - 0.62f * hudScale, HUD_Z); hudScore.characterSize = 0.060f * hudScale;
        hudBest.transform.localPosition  = new Vector3( ix, iy, HUD_Z); hudBest.characterSize = 0.055f * hudScale;
        gapText.transform.localPosition  = new Vector3( ix, iy - 0.55f * hudScale, HUD_Z); gapText.characterSize = 0.050f * hudScale;
        hudSpeed.transform.localPosition = new Vector3( ix, -iy, HUD_Z); hudSpeed.characterSize = 0.055f * hudScale;
        dbg.transform.localPosition      = new Vector3(-ix, -iy * 0.5f, HUD_Z); dbg.characterSize = 0.040f * hudScale;
        statusText.transform.localPosition = new Vector3(0, halfH * 0.30f, HUD_Z);
        if (statusPulse <= 0f) statusText.characterSize = 0.10f * hudScale;

        // LOCK strip: across the very top, just under the round/score text top edge
        float barW = halfW * 1.7f, barH = 0.26f * hudScale;
        float barY = iy - 1.35f * hudScale;
        LayoutBar(lockBarBG, lockBarFill, 0f, barY, barW, barH, Mathf.Clamp01(lockMeter / 100f));
        // ZONE proximity gauge: smaller, just below the lock strip
        float zW = halfW * 1.1f, zH = 0.14f * hudScale, zY = barY - 0.30f * hudScale;
        float zoneFrac = locked ? Mathf.Clamp01(1f - gap / ZONE_MAX) : 0f;
        LayoutBar(zoneBG, zoneFill, 0f, zY, zW, zH, zoneFrac);
    }

    void LayoutBar(Transform bg, Transform fill, float cx, float cy, float w, float h, float frac)
    {
        bg.localPosition = new Vector3(cx, cy, HUD_Z + 0.05f);
        bg.localScale = new Vector3(w, h, 1f);
        frac = Mathf.Clamp01(frac);
        fill.localScale = new Vector3(Mathf.Max(0.0001f, w * frac), h * 0.78f, 1f);
        fill.localPosition = new Vector3(cx - w * 0.5f + w * frac * 0.5f, cy, HUD_Z + 0.04f);
    }

    void RefreshHud()
    {
        if (hudRound) hudRound.text = "ROUND " + round;
        if (hudScore) hudScore.text = "SCORE  " + score;
        if (hudBest)  hudBest.text  = "BEST  " + best;
        if (hudSpeed) hudSpeed.text = Mathf.RoundToInt(speed * 3.6f) + " km/h";
    }

    // ===================================================================== input
    void GatherInput()
    {
        float key = Input.GetAxisRaw("Horizontal");
        float pointer = 0f; bool pressed = false; float px = 0f;
        if (Input.touchCount > 0) { pressed = true; px = Input.GetTouch(0).position.x; }
        else if (Input.GetMouseButton(0)) { pressed = true; px = Input.mousePosition.x; }
        if (pressed) { float n = (px / Mathf.Max(1f, Screen.width)) * 2f - 1f; pointer = Mathf.Clamp(n * 1.7f, -1f, 1f); }
        float raw = Mathf.Abs(key) > 0.01f ? key : pointer;

        if (Mathf.Abs(raw) > 0.01f || Input.anyKeyDown || Input.GetMouseButtonDown(0)) attract = false;
        if (attract) raw = AutoSteer();

        steerInput = Mathf.Clamp(raw, -1f, 1f);
    }

    float AutoSteer()
    {
        int look = (nearIdx + 7) % N;
        float want = HeadingFromTo(pos, pts[look]);
        float diff = Mathf.DeltaAngle(heading, want);
        return Mathf.Clamp(diff / 22f, -1f, 1f);
    }

    // ===================================================================== main loop
    void Update()
    {
        float dt = Time.deltaTime; if (dt > 0.05f) dt = 0.05f;
        sessionT += dt;

        if (Input.GetKeyDown(KeyCode.F1)) { showDbg = !showDbg; dbg.gameObject.SetActive(showDbg); }

        if (state == State.Over)
        {
            if (Input.GetKeyDown(KeyCode.R) || Input.GetKeyDown(KeyCode.Space) ||
                Input.GetMouseButtonDown(0) || Input.touchCount > 0) ResetRun(false);
            UpdateCamera(dt, false);
            AdjustHud();
            return;
        }

        GatherInput();
        runT += dt;

        int prevIdx = nearIdx;
        UpdateTrackPosition();
        UpdatePlayerArc();
        float gEst = SignedGap();      // last-frame gap, used to govern the throttle this frame
        float lateral = Vector3.Dot(pos - pts[nearIdx], leftN[nearIdx]);
        float absLat = Mathf.Abs(lateral);
        bool onRoad = absLat < HALF_W;
        float grassFactor = Mathf.Clamp01((absLat - HALF_W) / (SOFT_W - HALF_W));

        // ---------- arcade drift model ----------
        float driftAngle = Mathf.DeltaAngle(velAngle, heading);
        float absDrift = Mathf.Abs(driftAngle);
        // ADAPTIVE THROTTLE (cruise-control): aim to sit ~ZONE_GOOD behind the rival so you stay
        // in the zone by default. Drifting & grass scrub speed below this cap, dropping you back —
        // recovered by the throttle, but at high rounds the rival is near your top speed so a hard
        // slide can cost the lock. This is the core risk/reward: drift for points vs. keep the pace.
        float followGap = ZONE_GOOD;
        float targetSpeed = rivalSpeed + Mathf.Clamp((gEst - followGap) * 0.6f, -11f, 13f);
        float maxCap = Mathf.Lerp(MAX_SPEED, GRASS_MAX, grassFactor);
        maxCap *= 1f - Mathf.Clamp01(absDrift / 70f) * 0.30f;  // big slides scrub speed
        float cap = Mathf.Clamp(targetSpeed, 6f, maxCap);
        if (speed < cap) speed = Mathf.MoveTowards(speed, cap, ACCEL * dt);
        else             speed = Mathf.MoveTowards(speed, cap, ACCEL * 1.5f * dt);

        float spAuth = Mathf.Clamp01(speed / 7f) * Mathf.Lerp(1f, 0.78f, Mathf.Clamp01((speed - 18f) / 18f));
        heading += steerInput * TURN_RATE * spAuth * dt; heading = Norm(heading);

        float grip = Mathf.Lerp(240f, 78f, Mathf.Abs(steerInput));
        grip *= Mathf.Lerp(1f, 0.6f, grassFactor);
        velAngle = Mathf.MoveTowardsAngle(velAngle, heading, grip * dt);
        driftAngle = Mathf.DeltaAngle(velAngle, heading);
        if (Mathf.Abs(driftAngle) > 52f) velAngle = Norm(heading - Mathf.Sign(driftAngle) * 52f);

        Vector3 dir = Dir(velAngle);
        pos += dir * speed * dt; pos.y = 0f;

        float distO = Mathf.Sqrt(pos.x * pos.x + pos.z * pos.z);
        if (distO > 150f) { pos *= 150f / distO; speed *= 0.5f; }

        UpdatePlayerArc();
        UpdateRival(dt);
        SyncCar();
        UpdateChase(driftAngle, onRoad, grassFactor, dt);
        UpdateCones();
        UpdateCamera(dt, false);
        TickHud(dt);
        if (showDbg) UpdateDbg(lateral, driftAngle, grassFactor);
    }

    void UpdateTrackPosition()
    {
        float bestD = float.MaxValue; int bi = nearIdx;
        for (int k = -2; k <= 14; k++)
        {
            int i = ((nearIdx + k) % N + N) % N;
            float d = (pos - pts[i]).sqrMagnitude;
            if (d < bestD) { bestD = d; bi = i; }
        }
        nearIdx = bi;
    }

    void UpdatePlayerArc()
    {
        int i = nearIdx, j = (i + 1) % N;
        Vector3 seg = pts[j] - pts[i]; float segLen = seg.magnitude;
        float proj = segLen > 1e-4f ? Mathf.Clamp(Vector3.Dot(pos - pts[i], seg / segLen), 0f, segLen) : 0f;
        playerS = cum[i] + proj;
    }

    // ---- rival: driven along the centerline by arc length, slows in corners, mild rubber-band ----
    void UpdateRival(float dt)
    {
        // launch ramp: rival eases up to pace over the first ~2.5s so you're never instantly dropped
        float launch = Mathf.Clamp01(runT / 2.5f);
        rivalSpeed = rivalBase * Mathf.Lerp(0.55f, 1f, launch);
        float c = SampleCurv(rivalS);
        float local = rivalSpeed * Mathf.Lerp(1f, 0.72f, c);   // ease in sharp corners
        // mild rubber-band so a fallen-behind player can recover, and a clinging player gets pushed
        float g = SignedGap();
        if (g > 32f) local *= 0.86f;
        else if (g < 7f) local *= 1.06f;
        rivalS += local * dt;
        if (rivalS >= trackLen) rivalS -= trackLen;
        UpdateRivalTransform();
    }

    void UpdateRivalTransform()
    {
        Vector3 p, tan; SampleArc(rivalS, out p, out tan);
        rivalT.position = p;
        rivalT.rotation = Quaternion.LookRotation(tan, Vector3.up);
        // gentle cosmetic lean into corners
        float c = SampleCurv(rivalS);
        rivalVisual.localRotation = Quaternion.Slerp(rivalVisual.localRotation, Quaternion.Euler(0, 0, c * 8f), 1f - Mathf.Exp(-8f * Time.deltaTime));
    }

    // signed forward gap from player to rival, wrapped to [-trackLen/2, trackLen/2)
    float SignedGap()
    {
        float g = rivalS - playerS;
        g = Mathf.Repeat(g, trackLen);
        if (g > trackLen * 0.5f) g -= trackLen;
        return g;
    }

    // ===================================================================== chase / lock / scoring
    void UpdateChase(float driftAngle, bool onRoad, float grassFactor, float dt)
    {
        gap = SignedGap();
        bool inZone = gap >= -1.5f && gap <= ZONE_MAX;     // small tolerance for slight overtake
        locked = inZone;

        float absDrift = Mathf.Abs(driftAngle);
        bool slideNow = onRoad && speed > 9f && absDrift > DRIFT_DEG;
        drifting = slideNow;
        float targetDF = slideNow ? Mathf.Clamp01((absDrift - DRIFT_DEG) / 34f) : 0f;
        driftFactor = Mathf.MoveTowards(driftFactor, targetDF, dt * 3f);

        if (locked)
        {
            lockedTime += dt;
            // proximity: tighter gap = stronger. peak inside ZONE_GOOD.
            float prox = gap <= ZONE_GOOD ? Mathf.Lerp(1.0f, 2.0f, 1f - gap / ZONE_GOOD)
                                          : Mathf.Lerp(2.0f, 0.9f, (gap - ZONE_GOOD) / (ZONE_MAX - ZONE_GOOD));
            // LOCK meter charges; faster while drifting & tight
            float charge = (8f + driftFactor * 26f) * Mathf.Lerp(0.7f, 1.25f, Mathf.Clamp01(prox / 2f));
            lockMeter += charge * dt;

            // SCORE: continuous, scales with proximity, drift, round
            float roundMult = 1f + (round - 1) * 0.35f;
            float gain = (40f + driftFactor * 220f) * prox * roundMult * dt;
            scoreF += gain;
            int ns = Mathf.FloorToInt(scoreF);
            if (ns != score) { score = ns; if (score > best) { best = score; } }

            // tire smoke while sliding
            if (driftFactor > 0.25f)
            {
                smokeT -= dt;
                if (smokeT <= 0f)
                {
                    smokeT = 0.035f;
                    Vector3 rear = pos - Dir(heading) * 1.6f + Vector3.up * 0.3f;
                    Juice.Pop(rear, new Color(0.85f, 0.85f, 0.9f, 0.9f), 4);
                }
                Juice.Shake(Mathf.Min(0.03f + driftFactor * 0.10f, 0.16f));
            }

            redTint = Mathf.MoveTowards(redTint, 0f, dt * 2f);

            if (lockMeter >= 100f) ClearRound();
        }
        else
        {
            lockedTime = 0f;
            // out of the zone: drain. behind = lose ground; overtaken = wrong place.
            // first ~1.5s of a run is grace (you're spooling up to the rival's pace).
            float drain = (gap > ZONE_MAX) ? Mathf.Lerp(11f, 22f, Mathf.Clamp01((gap - ZONE_MAX) / 40f)) : 16f;
            if (runT < 1.5f) drain = 0f;
            lockMeter -= drain * dt;
            redTint = Mathf.MoveTowards(redTint, Mathf.Clamp01((100f - lockMeter) / 100f) * 0.9f + 0.15f, dt * 3f);
            if (lockMeter <= 0f) { lockMeter = 0f; GameOver(); return; }
        }

        UpdateStatus();
        RefreshHud();
    }

    void ClearRound()
    {
        round++;
        int bonus = 500 + round * 150;
        scoreF += bonus; score = Mathf.FloorToInt(scoreF);
        if (score > best) { best = score; PlayerPrefs.SetInt("tandemdrift_best", best); PlayerPrefs.Save(); }
        rivalBase = Mathf.Min(rivalBase + 1.6f, 33f); rivalSpeed = rivalBase;
        lockMeter = 58f;
        comboFlash = 1f; fovPunch = Mathf.Max(fovPunch, 7f);
        Juice.Score(pos + Vector3.up * 1.4f);
        Juice.Blip(720f + round * 40f, 0.09f, 0.45f); Juice.Blip(1100f, 0.08f, 0.35f);
        Juice.Shake(0.25f);
        Banner("ROUND " + round + "\nRIVAL FASTER!\n+" + bonus, new Color(1f, 0.85f, 0.3f), 1.8f);
        RefreshHud();
    }

    void UpdateStatus()
    {
        // suppress the centered status text while a transient banner (round clear) is on screen
        if (bannerTimer > 0f && bannerTimer < 900f) { statusText.text = ""; return; }
        if (locked)
        {
            if (drifting)
            {
                statusText.text = "TANDEM DRIFT!";
                statusText.color = new Color(1f, Mathf.Lerp(0.55f, 0.85f, Mathf.PingPong(sessionT * 4f, 1f)), 0.25f);
            }
            else
            {
                statusText.text = "LOCKED ON";
                statusText.color = new Color(0.5f, 1f, 0.8f);
            }
        }
        else
        {
            statusText.text = gap > ZONE_MAX ? "CATCH UP!" : "GET BEHIND!";
            statusText.color = new Color(1f, 0.4f, 0.35f);
        }
    }

    void GameOver()
    {
        state = State.Over;
        if (score > best) best = score;
        PlayerPrefs.SetInt("tandemdrift_best", best); PlayerPrefs.Save();
        Juice.Lose();
        Banner("CHASE LOST\nSCORE " + score + "\nTAP / R", new Color(1f, 0.55f, 0.5f), 999f);
        statusText.text = "";
        RefreshHud();
    }

    void SyncCar()
    {
        carT.position = pos; carT.rotation = Quaternion.Euler(0, heading, 0);
        float driftA = Mathf.DeltaAngle(velAngle, heading);
        float roll = Mathf.Clamp(-steerInput * 7f - driftA * 0.12f, -16f, 16f);
        float pitch = -Mathf.Clamp01(speed / MAX_SPEED) * 2.5f;
        carVisual.localRotation = Quaternion.Slerp(carVisual.localRotation, Quaternion.Euler(pitch, 0, roll), 1f - Mathf.Exp(-10f * Time.deltaTime));
    }

    // ===================================================================== cones
    void UpdateCones()
    {
        for (int i = 0; i < cones.Count; i++)
        {
            var c = cones[i];
            if (c.knocked || c.t == null) continue;
            Vector3 d = c.p - pos; d.y = 0f;
            if (d.sqrMagnitude < 2.4f * 2.4f && speed > 8f)
            {
                c.knocked = true;
                var fl = c.t.gameObject.AddComponent<Flyer>();
                Vector3 push = (d.sqrMagnitude > 0.01f ? d.normalized : Dir(velAngle));
                fl.Init(push * (3f + speed * 0.15f) + Vector3.up * 4f + Dir(velAngle) * speed * 0.2f);
                Juice.Blip(220f, 0.08f, 0.3f);
                Juice.Pop(c.p + Vector3.up * 0.4f, new Color(1f, 0.6f, 0.1f), 6);
                scoreF += 25; score = Mathf.FloorToInt(scoreF); comboFlash = 0.5f;
                RefreshHud();
            }
        }
    }

    // ===================================================================== camera / hud tick
    void UpdateCamera(float dt, bool snap)
    {
        if (cam == null) return;
        float targetYaw = velAngle;
        camYaw = snap ? targetYaw : Mathf.LerpAngle(camYaw, targetYaw, 1f - Mathf.Exp(-6f * dt));
        Vector3 back = Dir(camYaw);
        float aspect = Mathf.Max(0.3f, camComp.aspect);
        float distMul = aspect < 0.85f ? Mathf.Lerp(1.45f, 1.0f, Mathf.Clamp01((aspect - 0.45f) / 0.4f)) : 1f;
        float dist = (10.5f + Mathf.Clamp01(speed / MAX_SPEED) * 2.5f) * distMul;
        Vector3 want = pos - back * dist + Vector3.up * 4.8f * distMul;
        cam.position = snap ? want : Vector3.Lerp(cam.position, want, 1f - Mathf.Exp(-8f * dt));
        Vector3 look = pos + back * 6f + Vector3.up * 1.0f;
        Quaternion q = Quaternion.LookRotation(look - cam.position, Vector3.up);
        cam.rotation = snap ? q : Quaternion.Slerp(cam.rotation, q, 1f - Mathf.Exp(-9f * dt));

        fovPunch = Mathf.Lerp(fovPunch, 0f, 6f * dt);
        float baseFov = 58f + Mathf.Clamp01(speed / MAX_SPEED) * 14f;
        camComp.fieldOfView = Mathf.Clamp(baseFov + fovPunch, 50f, 88f);
        camComp.backgroundColor = Color.Lerp(new Color(0.58f, 0.70f, 0.86f), new Color(0.5f, 0.12f, 0.12f), redTint * 0.8f);
        AdjustHud();
    }

    void TickHud(float dt)
    {
        if (comboFlash > 0f) comboFlash -= dt * 2.0f;
        if (statusPulse > 0f) statusPulse -= dt;
        if (bannerTimer > 0f && bannerTimer < 900f)
        {
            bannerTimer -= dt;
            if (bannerTimer <= 0f) { bannerText.text = ""; bannerText.color = Color.white; }
        }
        // status text gentle pulse while drifting
        float pulse = drifting ? 1f + Mathf.PingPong(sessionT * 6f, 0.18f) : 1f;
        if (statusText) statusText.characterSize = 0.10f * hudScale * pulse;
        gapText.text = state == State.Over ? "" :
            (locked ? "GAP " + Mathf.RoundToInt(Mathf.Max(0f, gap)) + "m" : (gap > ZONE_MAX ? "+" + Mathf.RoundToInt(gap - ZONE_MAX) + "m BACK" : "AHEAD"));
        hudSpeed.text = Mathf.RoundToInt(speed * 3.6f) + " km/h";
        // lock fill color shifts toward red as it empties
        if (lockBarFill)
        {
            float f = Mathf.Clamp01(lockMeter / 100f);
            var mr = lockBarFill.GetComponent<MeshRenderer>();
            mr.sharedMaterial.color = Color.Lerp(new Color(1f, 0.35f, 0.3f, 0.95f), new Color(0.4f, 1f, 0.8f, 0.95f), f);
        }
    }

    void Banner(string s, Color c, float dur)
    {
        bannerText.transform.localPosition = new Vector3(0f, halfH * 0.02f, HUD_Z);
        bannerText.characterSize = 0.105f * hudScale;
        bannerText.text = s; bannerText.color = c; bannerTimer = dur;
    }

    void UpdateDbg(float lateral, float driftAngle, float grass)
    {
        dbg.text = string.Format(
            "spd {0:0.0} steer {1:0.00} drift {2:0.0} df {3:0.00}\nrivalSpd {4:0.0} gap {5:0.0} locked {6}\nlock {7:0.0} round {8} score {9}\nplayerS {10:0} rivalS {11:0} len {12:0}\nlat {13:0.0} grass {14:0.00} fps {15:0}",
            speed, steerInput, driftAngle, driftFactor, rivalSpeed, gap, locked,
            lockMeter, round, score, playerS, rivalS, trackLen, lateral, grass,
            1f / Mathf.Max(0.0001f, Time.smoothDeltaTime));
    }

    // ===================================================================== arc sampling
    void SampleArc(float s, out Vector3 p, out Vector3 tan)
    {
        s = Mathf.Repeat(s, trackLen);
        int start = rivalIdx;
        for (int k = 0; k < N; k++)
        {
            int a = (start + k) % N, b = (a + 1) % N;
            float ca = cum[a], cb = (b == 0) ? trackLen : cum[b];
            if (s >= ca && s < cb)
            {
                float segLen = cb - ca, t = segLen > 1e-4f ? (s - ca) / segLen : 0f;
                p = Vector3.Lerp(pts[a], pts[b], t);
                tan = pts[b] - pts[a]; tan.y = 0f;
                if (tan.sqrMagnitude < 1e-6f) tan = Vector3.forward; tan.Normalize();
                rivalIdx = a; return;
            }
        }
        p = pts[0]; tan = Vector3.forward;
    }

    float SampleCurv(float s)
    {
        s = Mathf.Repeat(s, trackLen);
        int start = rivalIdx;
        for (int k = 0; k < N; k++)
        {
            int a = (start + k) % N, b = (a + 1) % N;
            float ca = cum[a], cb = (b == 0) ? trackLen : cum[b];
            if (s >= ca && s < cb) { float t = (cb - ca) > 1e-4f ? (s - ca) / (cb - ca) : 0f; return Mathf.Lerp(curv[a], curv[b], t); }
        }
        return 0f;
    }

    // ===================================================================== math helpers
    static float Norm(float deg) { deg %= 360f; if (deg > 180f) deg -= 360f; else if (deg < -180f) deg += 360f; return deg; }
    static Vector3 Dir(float deg) { float r = deg * Mathf.Deg2Rad; return new Vector3(Mathf.Sin(r), 0f, Mathf.Cos(r)); }
    static float HeadingFromTo(Vector3 a, Vector3 b) { Vector3 d = b - a; return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; }
}

// short-lived tumbling object (knocked cone) — pure transform, self-destructs.
public class Flyer : MonoBehaviour
{
    Vector3 vel; Vector3 spin; float age, life = 1.6f;
    public void Init(Vector3 v) { vel = v; spin = new Vector3(Random.Range(-400f, 400f), Random.Range(-400f, 400f), Random.Range(-400f, 400f)); }
    void Update()
    {
        float dt = Time.deltaTime; age += dt;
        vel.y -= 16f * dt;
        transform.position += vel * dt;
        transform.Rotate(spin * dt, Space.World);
        if (transform.position.y < 0f && vel.y < 0f) { vel.y = -vel.y * 0.4f; vel.x *= 0.6f; vel.z *= 0.6f; }
        if (age >= life) Destroy(gameObject);
    }
}
