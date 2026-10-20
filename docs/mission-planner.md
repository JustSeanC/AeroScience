# Plan a first AeroScience mission

Mission Planner prepares the route, areas and intended science configuration. Mission Control starts/ends recording and shows live flight operations. Science Collection controls instruments and reviews their health and saved observations/products. A plan is optional; spontaneous recording remains available.

1. Open **Mission Planner** and give the plan a name. Choose Universal or Realistic mode and optional departure/destination. Less common fields, including the cruising altitude needed by PLN export, are under **Advanced mission details & export settings**.
2. Search an airport code (for example KJFK or JFK), airport name, city, or `latitude, longitude`. **Find** only lists results; select a result to center the map. Blank/no-match searches leave the view unchanged. **Center aircraft** needs fresh simulator position. The bundled OurAirports snapshot includes 72,684 open airports/heliports and other facilities; community data are not certified navigation information. No online geocoding account or API is involved.
3. Choose **Add waypoint**, then click the map. The highlighted editor and temporary marker belong to an unsaved point. Edit its name, position, optional altitude/instructions and click **Save Waypoint**. This commits the point to the current plan draft. **Cancel Waypoint** leaves the route unchanged. Repeat for a second point. Existing points can be selected, dragged, edited, reordered or removed. Normal map clicks/panning do not create points.
4. To survey a region, choose **Draw Survey Area** and click around its boundary. **Undo Last Vertex** corrects a click. With at least three distinct vertices, **Finish Area** closes a preview and reports approximate spherical area/E–W/N–S dimensions. Give it a name and **Save Survey Area**. Crossings, duplicate/invalid points and unusable area are rejected. **Cancel Drawing/Cancel Survey Area** leaves the plan geometry unchanged. Manual vertex editing is under Advanced.
5. Expand **Flight pattern generator**. Choose the center on the map or select a saved survey area and **Fit Pattern to Survey Area**. Parameter changes automatically update a dashed white preview; they do not change the committed gold route or force a new map view. **Fit Preview** deliberately changes the view. Heading, dimensions, spacing, legs, radius, direction and revolutions all participate in the same debounced preview. **Add Pattern to Mission** commits it to the draft; **Discard Preview** leaves the committed route unchanged.
6. Lawnmower long legs follow the reference heading and step sideways. Creeping-line long legs cross that heading and step forward. Blue reference arrow, pink step segment, green entry and pink exit explain the preview. The extent outline is not a promise of sensor coverage. Survey fitting uses a heading-aligned local envelope derived with the existing WGS84 geodesics. It preserves the entered spacing, calculates the required passes and adjusts the center/scan length. Routes are not clipped to an irregular polygon and can extend beyond its boundary. Reduce the area or increase spacing if it would need more than 100 passes; local extents are bounded to 500 km.
7. An orbit keeps its center/radius/direction/revolutions as a semantic definition. The map draws its circular outline and entry/exit markers. Its numbered navigation points are collapsed under an Advanced list. Point-to-point exports preserve the existing 5° approximation: normally 73 points for one revolution. Export shows the actual point count and compatibility warning; no native EFB continuous-orbit command is claimed.
8. Configure instruments and review **Scientific Data Readiness**. Automatic scientific access has separate consent from online basemaps. External dataset observation dates differ from the simulated flight time. See the [automatic-data quick start](remote-sensing/quick-start.md).
9. **Save mission plan** persists the complete draft. Save/cancel any temporary point/polygon and add/discard a pattern preview first. The persistent status distinguishes draft points, plan edits, previews and a saved plan. Switching dashboard sections keeps the draft in this browser tab. Opening another plan asks before discarding edits; reloading/leaving warns. Drafts are not a substitute for saving and do not survive a browser crash.
10. **Export / Share Route** previews the complete route before download/copy. PLN needs a cruising altitude. Check target EFB point limits. Saved plans can be started with **Record this plan**; Mission Control and the toolbar use the same immutable recording snapshot.

The shared map layer control separates planned route, semantic pattern outlines, waypoint markers, recorded track, survey areas, aircraft, observation markers, successful scientific sample locations and saved remote-sensing footprint outlines. Blue sample points require valid saved scientific values. A footprint can contain excluded cloud/nodata pixels and is not a live camera image or proof of complete coverage. **Scientific sample locations** in Science Collection is expandable rather than another always-open map.

Dashboard Settings actions share one auxiliary native dialog. Repeated requests focus the existing window; conflicting work waits. Owned file/folder pickers and legitimate Settings children remain available. Other dashboard clients can continue viewing telemetry/history.

## First-time-user acceptance still required

Browser regressions exercise searching, point commit/cancel, two-point saving/export, polygon validation, live pattern changes, stale-response rejection, orbit display/export and saved-plan/toolbar fixtures. These are automated checks with synthetic flight inputs. They do not establish that an uncoached person completed the workflow.

On Windows, ask a first-time tester to perform the five scenarios in the [v0.3.0 Windows guide](https://github.com/JustSeanC/AeroScience/blob/8f510f02f4d9b41fba4e55fefa950b82e48c132d/docs/v0.3.0-windows-testing.md): simple two-point flight, geographic survey, orbit, live route/collection and repeated native Settings actions from two browsers. Record where coaching was needed. Genuine MSFS window ownership/focus, toolbar route, EFB import, installation/upgrade, Quest and physical iPad acceptance remain pending.

Larger semantic export changes, additional online geocoders and a wider dashboard redesign are P2 backlog. Existing export precision, saved snapshots, modes, map preferences and native SDK window handling remain intact.


## Aviation and metric planning units

**Planning units** is available in Mission Planner and Settings' display-unit controls. New installations default to **Aviation**: horizontal distances in NM, altitude in feet MSL and speed in knots where shown. Headings remain degrees **true**, never silently magnetic. Existing explicit choices remain; a mixed/older selection is shown as Custom rather than replaced.

**Metric** displays horizontal distances in m or km according to scale, altitude in meters MSL, applicable speed in km/h and survey area in m²/km². Aviation area is NM². Labels identify every field's unit. Switching profiles converts entered values and summaries using the existing shared display preferences. Saved pattern dimensions, GeographicLib computations and canonical exports remain meters/feet/WGS84 as before. Untouched fields retain their full canonical precision through repeated switches; displayed decimal rounding does not rewrite that value.

For lawnmower and creeping line, **Track Spacing (NM)** is the highlighted primary field. For example **0.25 NM = 463 m**. Change spacing or survey width to recalculate passes, the live map preview, route distance and approximate survey envelope. The pass count is calculated for parallel surveys; waypoint count remains an export detail. **Fit Pattern to Survey Area** uses the selected polygon, heading and entered spacing to initialize parallel legs. Its displayed envelope is not a claim of complete scientific coverage.

Future **v0.4.0** work may recommend spacing from a configured sensor's actual or defensibly calculated ground footprint and user-selected overlap. No invented swath width, automatic overlap recommendation or speculative coverage score is implemented in v0.3.0.

## v0.3.1 planning corrections

Planning exposes Aviation and Metric only. Existing custom scientific display preferences are retained separately. Canonical route/pattern geometry is unchanged by profile switches. Pattern-specific controls validate near the relevant field in current display units; an invalid edit retains the previous preview as outdated. Add Pattern to Mission commits the matching validated preview once, preserving manual waypoints; Save Mission Plan persists the resulting draft. Fit Preview changes the viewport deliberately.

Scientific preflight advances in bounded batches beyond the initial window. During flight, actual geographic motion takes priority over the optional saved route. Readiness is preparation status, not proof of complete sensor coverage. See [scientific flying](remote-sensing/quick-start.md).


## v0.3.2 polygon scan fitting

Fit Pattern to Survey Area now defaults to **Polygon scan legs** for lawnmower/creeping-line patterns. Green scans intersect the actual local survey ring; orange dashed connectors can leave concave boundaries. Aircraft turning radius, terrain clearance and complete sensor coverage are not validated. **Rectangular envelope overscan** remains an explicit alternative. Existing saved patterns keep their old envelope behavior until deliberately edited. True headings, NM/Metric conversions, WGS84 coordinates and full route exports remain.

Preflight preparation is a separate deliberate review/download action; saving a plan does not contact scientific providers. See Download Activity for session controls and separate preflight/flight allowance figures.
