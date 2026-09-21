using UnityEngine;

namespace WW2.SphericalTerrainPreview
{
    /// <summary>One radial orbit camera for every distance. The terrain never changes projection.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(Camera))]
    public sealed class SphericalPreviewCamera : MonoBehaviour
    {
        public SphericalTerrainPreview terrain;
        public SphericalPreviewHUD hud;
        public Light sun;
        [Range(.05f, .5f)] public float damping = .18f;
        [Min(.01f)] public float moveSmoothTime = .12f;
        [Tooltip("Distance-proportional pan speed, matching Game_2's camera feel.")]
        [Min(.05f)] public float moveSpeedRatio = .9904f;
        [Min(1f)] public float rotationSpeed = 180f;
        [Min(.05f)] public float pointerPanLongPressSeconds = .18f;
        [Range(5f, 85f)] public float sunElevation = 48f;
        public bool InputEnabled = true;
        // Used by owned capture/replay tools without changing the game's UI locks.
        public bool SuppressUserInput { get; set; }
        public bool PointerInputEnabled = true;
        public bool SelectPreviewCells = true;
        public bool AllowRightDrag = true;
        public bool AllowLeftDrag = true;
        public System.Func<Vector2, bool> PointerOverUI;
        public bool IsPointerPanning => dragging && moved;
        public bool ShouldSuppressClick => moved || Time.frameCount <= suppressClickThroughFrame;
        int suppressClickThroughFrame = -1;
        public float initialLongitude = 10f, initialLatitude = 46f, initialAltitude = 85f;
        public const float MinimumAltitude = 20f, MaximumAltitude = 9000f;
        // Geographic north has no unique direction at the pole itself. Keep
        // the orbit focus just inside it instead of flipping to the opposite
        // meridian; both poles remain visible from this small polar margin.
        public const float MaximumFocusLatitude = 89.9f;
        public Camera PresentationCamera { get; private set; }
        public Vector3 FocusDirection { get; private set; }
        public float Altitude => Mathf.Exp(logAltitude);
        public float TargetAltitude => Mathf.Exp(targetLogAltitude);
        public float Longitude => focusCoordinates.x;
        public float Latitude => focusCoordinates.y;
        public bool IsSettled => Mathf.Abs(logAltitude - targetLogAltitude) < .002f && (FocusDirection - targetFocus).sqrMagnitude < 1e-9f;

        Vector3 targetFocus, dragAnchor, zoomAnchor;
        Vector3 cameraNorth;
        Vector2 focusCoordinates, targetCoordinates;
        Vector2 pressedPosition, zoomScreen;
        Vector2 smoothedMoveInput, moveInputVelocity;
        float logAltitude, targetLogAltitude, logVelocity;
        float pressTime, streamDampFactor = 1f;
        float cameraBaseHeight, baseHeightVelocity;
        float headingDegrees;
        bool dragging, moved, anchoredZoom;
        int dragButton;

        void Awake()
        {
            PresentationCamera = GetComponent<Camera>();
            SetView(initialLongitude, initialLatitude, initialAltitude, true);
            Application.runInBackground = true;
        }

        public static Vector3 Direction(float longitude, float latitude)
        {
            float lon = longitude * Mathf.Deg2Rad, lat = latitude * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(lat) * Mathf.Cos(lon), Mathf.Sin(lat), Mathf.Cos(lat) * Mathf.Sin(lon));
        }

        public void SetView(float longitude, float latitude, float altitude, bool immediate = true)
            => SetCoordinateView(new Vector2(longitude, latitude), altitude, immediate);

        public void SetDirectionView(Vector3 direction, float altitude, bool immediate = true)
        {
            if (direction.sqrMagnitude < .5f) return;
            SetCoordinateView(Coordinates(direction.normalized), altitude, immediate);
        }

        void SetCoordinateView(Vector2 coordinates, float altitude, bool immediate)
        {
            targetCoordinates = ClampCoordinates(coordinates);
            targetFocus = Direction(targetCoordinates.x, targetCoordinates.y);
            targetLogAltitude = Mathf.Log(Mathf.Clamp(altitude, MinimumAltitude, MaximumAltitude));
            CancelPointerGesture();
            anchoredZoom = false;
            smoothedMoveInput = moveInputVelocity = Vector2.zero;
            headingDegrees = 0;
            if (immediate)
            {
                SetPresentedCoordinates(targetCoordinates); logAltitude = targetLogAltitude; logVelocity = 0f;
                smoothedMoveInput = moveInputVelocity = Vector2.zero;
                UpdateGroundReference(0, true);
                if (!PresentationCamera) PresentationCamera = GetComponent<Camera>();
                ApplyPose();
            }
        }

        public void ApplyPreset(string preset, bool immediate = false)
        {
            switch (preset)
            {
                case "globe": SetView(20f, 30f, 7000f, immediate); break;
                case "europe": SetView(10f, 46f, 85f, immediate); break;
                case "eastasia": SetView(110f, 30f, 100f, immediate); break;
                case "forest": SetView(8.2f, 48.2f, 100f, immediate); break;
                case "desert": SetView(15f, 24f, 85f, immediate); break;
                case "plateau-river": SetView(90f, 29.4f, 85f, immediate); break;
                case "plateau-edge": SetView(-100.36090f, 41.58260f, 65f, immediate); break;
                case "desert-plateau": SetView(91.439f, 41.72051f, 65f, immediate); break;
                case "single-mountain": SetView(-65.00376f, -32.305126f, 55f, immediate); break;
                // Kunlun margin: both upper platform edges sit inside the
                // near frame, with open ground in front of the snow mountains.
                case "tibet": SetView(83.90f, 35.97f, 75f, immediate); break;
                case "tibet-wide": SetView(83.90f, 35.97f, 110f, immediate); break;
                case "coast": SetView(5.2f, 60.1f, 75f, immediate); break;
                case "river": SetView(31.1f, 30.2f, 80f, immediate); break;
                case "continent": SetView(10f, 46f, 900f, immediate); break;
                case "pentagon":
                    if (terrain && terrain.World != null && terrain.World.Pentagons.Length > 0)
                    {
                        int pentagon = terrain.World.Pentagons[0];
                        foreach (int candidate in terrain.World.Pentagons)
                            if (!terrain.World.Cells[candidate].Water) { pentagon = candidate; break; }
                        SetDirectionView(terrain.World.Centers[pentagon], 65f, immediate);
                        terrain.SetSelection(pentagon);
                        terrain.SetGridVisible(true);
                        if (hud) hud.GridVisible = true;
                    }
                    break;
            }
        }

        void LateUpdate()
        {
            if (!terrain) return;
            float dt = Mathf.Min(Time.unscaledDeltaTime, .1f);
            if (InputEnabled && !SuppressUserInput && Application.isFocused)
            {
                HandleKeyboard(Mathf.Min(dt, 1f / 30f));
                HandleInput();
            }
            else
            {
                // Programmatic validation uses ApplyScroll with input locked;
                // its anchor must finish through the same damped camera path.
                CancelPointerGesture();
                smoothedMoveInput = moveInputVelocity = Vector2.zero;
            }
            logAltitude = Mathf.SmoothDamp(logAltitude, targetLogAltitude, ref logVelocity, damping, float.PositiveInfinity, dt);
            float follow = 1f - Mathf.Exp(-dt / damping);
            SetPresentedCoordinates(new Vector2(Mathf.LerpAngle(Longitude, targetCoordinates.x, follow),
                Mathf.Lerp(Latitude, targetCoordinates.y, follow)));
            UpdateGroundReference(dt);
            ApplyPose();
            if (anchoredZoom && !dragging)
            {
                // Re-intersect after each damped distance change so the same surface point stays under the cursor.
                for (int iteration = 0; iteration < 2; iteration++)
                {
                    if (!PickDirection(zoomScreen, out Vector3 underPointer)) break;
                    AlignGeographicAnchor(underPointer, zoomAnchor);
                    ApplyPose();
                }
                if (Mathf.Abs(logAltitude - targetLogAltitude) < .0005f) anchoredZoom = false;
            }
            terrain.SetFocus(FocusDirection, Altitude);
        }

        void ApplyPose()
        {
            if (!PresentationCamera || !terrain) return;
            Vector3 up = FocusDirection;
            if (up.sqrMagnitude < .5f) up = Vector3.right;
            // Movement never transports or accumulates camera bearing. Only
            // explicit Q/E input rotates the fixed geographic north frame.
            cameraNorth = Quaternion.AngleAxis(headingDegrees, up) * GeographicNorth(up);
            Vector3 north = cameraNorth;
            float tilt = Mathf.Lerp(37f, 0f, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(130f, 1800f, Altitude)));
            Vector3 focus = up * (terrain.radius + cameraBaseHeight + 1.5f);
            Vector3 offset = up * Altitude - north * (Altitude * Mathf.Tan(tilt * Mathf.Deg2Rad));
            transform.SetPositionAndRotation(focus + offset, Quaternion.LookRotation(-offset, north));
            PresentationCamera.nearClipPlane = Mathf.Lerp(.12f, 8f, Mathf.InverseLerp(300f, 7000f, Altitude));
            PresentationCamera.farClipPlane = terrain.radius * 2.5f + MaximumAltitude;
            if (sun)
            {
                Vector3 sunNorth = GeographicNorth(up);
                Vector3 east = Vector3.Cross(sunNorth, up).normalized;
                Vector3 towardSun = up * Mathf.Sin(sunElevation * Mathf.Deg2Rad)
                    + (sunNorth * .65f - east * .76f).normalized * Mathf.Cos(sunElevation * Mathf.Deg2Rad);
                sun.transform.rotation = Quaternion.LookRotation(-towardSun, sunNorth);
            }
        }

        void UpdateGroundReference(float deltaTime, bool immediate = false)
        {
            // A plateau is a raised ground level. The orbit follows that level
            // smoothly, never the individual mountain peaks or streamed LODs.
            float target = terrain && terrain.Surface != null && Altitude < 650f
                ? terrain.Surface.BaseHeight(FocusDirection) : 0;
            if (immediate) { cameraBaseHeight = target; baseHeightVelocity = 0; }
            else cameraBaseHeight = Mathf.SmoothDamp(cameraBaseHeight, target, ref baseHeightVelocity, .16f,
                float.PositiveInfinity, Mathf.Min(deltaTime, .1f));
        }

        void HandleInput()
        {
            Vector2 pointer = Input.mousePosition;
            if (!PresentationCamera.pixelRect.Contains(pointer)) { CancelPointerGesture(); return; }
            bool overUi = !PointerInputEnabled || (hud && hud.IsPointerOverUi(pointer)) || (PointerOverUI?.Invoke(pointer) ?? false);
            if (overUi) { CancelPointerGesture(); return; }
            float scroll = Input.mouseScrollDelta.y;
            if (!overUi && Mathf.Abs(scroll) > .001f)
                ApplyScroll(scroll, pointer);
            for (int button = 0; button < 3; button++)
            {
                if ((button == 1 && !AllowRightDrag) || (button == 0 && !AllowLeftDrag)) continue;
                if (!overUi && Input.GetMouseButtonDown(button) && PickDirection(pointer, out dragAnchor))
                {
                    dragging = true; dragButton = button; pressedPosition = pointer; moved = false; anchoredZoom = false;
                    pressTime = Time.unscaledTime;
                    targetCoordinates = focusCoordinates; targetFocus = FocusDirection;
                }
            }
            if (dragging && Input.GetMouseButton(dragButton))
            {
                bool pastThreshold = (pointer - pressedPosition).sqrMagnitude > 16f;
                moved |= pastThreshold && (dragButton != 0 || Time.unscaledTime - pressTime >= pointerPanLongPressSeconds);
                if (moved)
                {
                    anchoredZoom = false;
                    for (int iteration = 0; iteration < 2; iteration++)
                    {
                        if (!PickDirection(pointer, out Vector3 current)) break;
                        AlignGeographicAnchor(current, dragAnchor);
                        ApplyPose();
                    }
                }
            }
            if (dragging && !Input.GetMouseButton(dragButton))
            {
                if (moved) suppressClickThroughFrame = Time.frameCount + 1;
                if (SelectPreviewCells && Input.GetMouseButtonUp(dragButton) && dragButton == 0 && !moved && (pointer - pressedPosition).sqrMagnitude <= 16f
                    && terrain.TryPick(PresentationCamera.ScreenPointToRay(pointer), out int cell, out _))
                    terrain.SetSelection(cell);
                dragging = moved = false;
            }
        }

        void CancelPointerGesture()
        {
            if (moved) suppressClickThroughFrame = Time.frameCount + 1;
            dragging = moved = false;
        }

        void HandleKeyboard(float deltaTime)
        {
            // Explicit keys work in the isolated scene without depending on a
            // custom Input Manager axis from the standalone map project.
            Vector2 input = new(
                (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow) ? 1 : 0)
                    - (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow) ? 1 : 0),
                (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow) ? 1 : 0)
                    - (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow) ? 1 : 0));
            input = Vector2.ClampMagnitude(input, 1f);
            smoothedMoveInput = Vector2.SmoothDamp(smoothedMoveInput, input, ref moveInputVelocity,
                Mathf.Max(.01f, moveSmoothTime), float.PositiveInfinity, deltaTime);
            if (smoothedMoveInput.sqrMagnitude > .0001f) ApplyPan(smoothedMoveInput, deltaTime);
            else if (input == Vector2.zero) smoothedMoveInput = moveInputVelocity = Vector2.zero;
            float rotation = (Input.GetKey(KeyCode.E) ? 1 : 0) - (Input.GetKey(KeyCode.Q) ? 1 : 0);
            if (rotation != 0) ApplyRotation(rotation * rotationSpeed * deltaTime);
        }

        /// <summary>Distance-scaled map pan with a fixed geographic bearing.</summary>
        public void ApplyPan(Vector2 input, float deltaTime)
        {
            if (!terrain || input.sqrMagnitude < .000001f) return;
            input = Vector2.ClampMagnitude(input, 1f);
            float dt = Mathf.Clamp(deltaTime, 0, 1f / 30f);
            streamDampFactor = Mathf.MoveTowards(streamDampFactor, terrain.DetailLoading ? .78f : 1f, dt * 2.5f);
            float speed = Altitude * Mathf.Max(.05f, moveSpeedRatio) * streamDampFactor;
            Vector3 up = FocusDirection;
            Vector3 geographicNorth = GeographicNorth(up);
            Vector3 east = Vector3.Cross(up, geographicNorth).normalized;
            Vector3 north = Quaternion.AngleAxis(headingDegrees, up) * geographicNorth;
            // Camera forward points inward: Cross(up,north) is screen right.
            // The former reversed cross product made D move left and A right.
            Vector3 right = Vector3.Cross(up, north).normalized;
            Vector3 tangent = right * input.x + north * input.y;
            float angularDistance = speed * dt / Mathf.Max(terrain.radius, 1f) * Mathf.Rad2Deg;
            // Keep horizontal speed bounded in the tiny polar margin. At the
            // default bearing A/D follow parallels and W/S follow meridians.
            float parallelScale = Mathf.Max(.05f, Mathf.Cos(Latitude * Mathf.Deg2Rad));
            MoveToCoordinates(new Vector2(Longitude + Vector3.Dot(tangent, east) * angularDistance / parallelScale,
                Latitude + Vector3.Dot(tangent, geographicNorth) * angularDistance));
            CancelPointerGesture(); anchoredZoom = false;
            ApplyPose();
            terrain.SetFocus(FocusDirection, Altitude);
        }

        /// <summary>Q/E rotates the view around the current radial up, as in the standalone HexMap camera.</summary>
        public void ApplyRotation(float degrees)
        {
            headingDegrees = Mathf.Repeat(headingDegrees + degrees + 180f, 360f) - 180f;
            CancelPointerGesture(); anchoredZoom = false;
            ApplyPose();
        }

        static Vector3 GeographicNorth(Vector3 direction)
        {
            Vector2 coordinates = Coordinates(direction);
            float longitude = coordinates.x * Mathf.Deg2Rad, latitude = coordinates.y * Mathf.Deg2Rad;
            return new Vector3(-Mathf.Sin(latitude) * Mathf.Cos(longitude), Mathf.Cos(latitude),
                -Mathf.Sin(latitude) * Mathf.Sin(longitude));
        }

        static Vector2 Coordinates(Vector3 direction) => new Vector2(
            Mathf.Atan2(direction.z, direction.x) * Mathf.Rad2Deg,
            Mathf.Atan2(direction.y, Mathf.Sqrt(direction.x * direction.x + direction.z * direction.z)) * Mathf.Rad2Deg);

        static Vector2 ClampCoordinates(Vector2 coordinates) => new Vector2(
            Mathf.Repeat(coordinates.x + 180f, 360f) - 180f,
            Mathf.Clamp(coordinates.y, -MaximumFocusLatitude, MaximumFocusLatitude));

        void SetPresentedCoordinates(Vector2 coordinates)
        {
            focusCoordinates = ClampCoordinates(coordinates);
            FocusDirection = Direction(Longitude, Latitude);
        }

        void MoveToCoordinates(Vector2 coordinates)
        {
            SetPresentedCoordinates(coordinates);
            targetCoordinates = focusCoordinates; targetFocus = FocusDirection;
        }

        void AlignGeographicAnchor(Vector3 current, Vector3 anchor)
        {
            Vector2 from = Coordinates(current), to = Coordinates(anchor);
            MoveToCoordinates(new Vector2(Longitude + Mathf.DeltaAngle(from.x, to.x), Latitude + to.y - from.y));
        }

        /// <summary>The same anchored input path used by the wheel, also available to fixed preview validation.</summary>
        public void ApplyScroll(float steps, Vector2 screenPoint)
        {
            anchoredZoom = false;
            // Zoom in still follows the pointer. Zoom out keeps the geographic
            // focus instead of rotating the globe toward an off-centre cursor.
            if (steps > 0 && PickDirection(screenPoint, out zoomAnchor)) { zoomScreen = screenPoint; anchoredZoom = true; }
            if (steps < 0)
            {
                targetCoordinates = focusCoordinates;
                targetFocus = FocusDirection;
            }
            targetLogAltitude = Mathf.Clamp(targetLogAltitude - steps * .16f, Mathf.Log(MinimumAltitude), Mathf.Log(MaximumAltitude));
            // Fast reversals change the target immediately; discard momentum that still points the other way.
            if (Mathf.Sign(logVelocity) != Mathf.Sign(targetLogAltitude - logAltitude)) logVelocity = 0f;
        }

        bool PickDirection(Vector2 screen, out Vector3 direction)
        {
            direction = Vector3.zero;
            if (!PresentationCamera || !terrain) return false;
            Ray ray = PresentationCamera.ScreenPointToRay(screen);
            // The orbit anchor is the unchanged reference sphere, independent of which LOD is currently ready.
            float b = Vector3.Dot(ray.origin, ray.direction);
            float c = ray.origin.sqrMagnitude - terrain.radius * terrain.radius;
            float discriminant = b * b - c;
            if (discriminant < 0f) return false;
            float t = -b - Mathf.Sqrt(discriminant);
            if (t <= 0f) return false;
            direction = ray.GetPoint(t).normalized;
            return true;
        }
    }
}
