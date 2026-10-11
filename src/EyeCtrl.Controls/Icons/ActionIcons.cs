namespace EyeCtrl.Controls.Icons;

using System.Collections.Generic;
using System.Windows.Media;
using EyeCtrl.Core.Models;

/// <summary>
/// The action menu's drawn icon set: <see cref="ActionIcon"/> to WPF <see cref="Geometry"/>.
///
/// <para>
/// <b>Everything is authored on a <see cref="GridSize"/>-unit square at
/// <see cref="GridStrokeThickness"/> stroke,</b> and scaled to the target by the caller's
/// <c>RenderTransform</c>. RenderTransform scales the stroke along with the geometry;
/// <c>Geometry.Transform</c> would not, and would leave a 1.8px hairline on a 63px icon.
/// </para>
///
/// <para>
/// <b>Most actions are mouse actions, so most icons are mice.</b> The deliberate exceptions —
/// <see cref="ActionIcon.More"/> (navigation, not a click), <see cref="ActionIcon.Keyboard"/>
/// and <see cref="ActionIcon.KeyboardEdge"/> (keyboard commands, not mouse actions), and
/// <see cref="ActionIcon.Recapture"/> (re-shoots the screen region, not a click) — break
/// the mouse silhouette on purpose: drawing them as mice would say they perform a click.
/// Modifiers sit in the margins and never on the body: scroll variants take chevrons at the
/// right, drag takes motion bars at the left. <see cref="ActionIcon.DoubleClick"/> is the
/// other exception — it draws two mice rather than one, so it differs from
/// <see cref="ActionIcon.LeftClick"/> in outline rather than in detail, because with labels
/// hidden a mark inside the same silhouette is invisible in peripheral vision, which is where
/// a gaze user finds a target before looking straight at it.
/// </para>
///
/// <para>
/// Geometries are parsed once, frozen, and shared. Frozen is not an optimisation here: these are
/// statics handed to every button on screen, and an unfrozen Geometry is both mutable and
/// thread-affine.
/// </para>
/// </summary>
public static class ActionIcons
{
    /// <summary>
    /// The app's icon grid: every icon registry (<see cref="ActionIcons"/> and
    /// <see cref="ToolbarIcons"/>) authors its paths on this size square, in grid units, and
    /// <see cref="EyeCtrl.Controls.DwellArcButton"/> scales from it.
    /// </summary>
    public const double GridSize = 24.0;

    /// <summary>Stroke width in grid units, before the caller's scale transform.</summary>
    public const double GridStrokeThickness = 1.8;

    /// <summary>
    /// The plain mouse outline the scroll icons draw — a 10 x 19 stadium at x=7, y=2.5. Also
    /// shared by <see cref="ToolbarIcons"/>' <c>ProviderMouse</c>, which draws one plain mouse
    /// rather than a second, slightly-different one. The click icons do NOT use it since
    /// 2026-10-06: they draw the "popped" family below, whose pressed side changes the outline.
    /// </summary>
    internal const string Body = "M7,7.5 A5,5 0 0 1 17,7.5 L17,16.5 A5,5 0 0 1 7,16.5 Z";

    /// <summary>
    /// A short wheel stroke on the scroll icons' plain mouse. Also shared by
    /// <see cref="ToolbarIcons"/>' <c>ProviderMouse</c>.
    /// </summary>
    internal const string Wheel = " M12,6 L12,9.5";

    // ---- The "popped" mouse family (user pick, 2026-10-06) ----
    //
    // The pressed button is a solid piece lifted two grid units out of the body, so the pressed
    // side changes the OUTLINE rather than only a fill inside it — with the labels gone, a mark
    // inside the same silhouette is invisible in peripheral vision, where a gaze user finds a
    // target before saccading to it. The body is a 12 x 19 stadium (x6..18, y3.5..22.5) with its
    // button divider at y12.5 and the socket open where the cap lifts out.

    private const string PoppedLeftBody = "M12,3.5 A6,6 0 0 1 18,9.5 L18,16.5 A6,6 0 0 1 6,16.5 L6,12.5 M6,12.5 L18,12.5 M12,3.5 L12,12.5";
    private const string PoppedRightBody = "M12,3.5 A6,6 0 0 0 6,9.5 L6,16.5 A6,6 0 0 0 18,16.5 L18,12.5 M6,12.5 L18,12.5 M12,3.5 L12,12.5";
    private const string PoppedLeftFill = "M10,1.5 A6,6 0 0 0 4,7.5 L4,10.5 L10,10.5 Z";
    private const string PoppedRightFill = "M14,1.5 A6,6 0 0 1 20,7.5 L20,10.5 L14,10.5 Z";

    /// <summary>The middle variant: the body's top leaves a socket and the wheel pillar rises through it.</summary>
    private const string PoppedMiddleBody = "M15.3,4.49 A6,6 0 0 1 18,9.5 L18,16.5 A6,6 0 0 1 6,16.5 L6,9.5 A6,6 0 0 1 8.7,4.49 M6,12.5 L18,12.5";
    private const string PoppedWheelFill = "M10.1,2.9 A1.9,1.9 0 0 1 13.9,2.9 L13.9,7.1 A1.9,1.9 0 0 1 10.1,7.1 Z";

    /// <summary>
    /// Double click's "two mice": the front mouse, left button filled in place, plus a back mouse
    /// peeking out up-left, as two clicks drawn as two of the thing. The fill is a serialized
    /// union (the back silhouette outside the front's widened outline) long enough to live beside
    /// the map below, so the hand-authored constants stay readable.
    /// </summary>
    private const string TwoMiceStroke = "M8,9A5.5,5.5,0,0,1,19,9L19,17A5.5,5.5,0,0,1,8,17z M13.5,3.5L13.5,12.5 M8,12.5L19,12.5";

    private const string TwoMiceFill = "F1M13.5,3.5L13.5,12.5 8,12.5 8,9C8,5.962433815002441,10.462433815002441,3.5,13.5,3.5z M10,0.29999983310699463L10.164752006530762,0.3020848333835602 10.328592300415039,0.3083103597164154 10.491455078125,0.31863120198249817 10.653275489807129,0.33300116658210754 10.813989639282227,0.3513730466365814 10.973535537719727,0.3736977279186249 11.13184928894043,0.39992740750312805 11.288869857788086,0.4300115704536438 11.444537162780762,0.4638996720314026 11.59879207611084,0.501541256904602 11.751575469970703,0.5428849458694458 11.902830123901367,0.5878788232803345 12.052499771118164,0.6364702582359314 12.200528144836426,0.6886082291603088 12.346861839294434,0.7442401647567749 12.491445541381836,0.8033130168914795 12.63422679901123,0.8657752871513367 12.775154113769531,0.9315745234489441 12.914175033569336,1.0006589889526367 13.051239013671875,1.07297682762146 13.186295509338379,1.1484757661819458 13.27906608581543,1.203321933746338 13.100532531738281,1.2101061344146729 12.902328491210938,1.222666621208191 12.70527458190918,1.2401654720306396 12.509468078613281,1.262548804283142 12.315003395080566,1.289759874343872 12.12197208404541,1.3217415809631348 11.930464744567871,1.3584333658218384 11.740567207336426,1.3997735977172852 11.55236530303955,1.4456992149353027 11.365941047668457,1.4961462020874023 11.18137264251709,1.5510495901107788 10.998739242553711,1.610343337059021 10.818115234375,1.673962116241455 10.639573097229004,1.7418384552001953 10.46318244934082,1.8139067888259888 10.28901195526123,1.8901009559631348 10.117128372192383,1.9703539609909058 9.947595596313477,2.054600715637207 9.856973648071289,2.102414131164551 9.762533187866211,2.1060023307800293 9.645110130310059,2.11344313621521 9.528589248657227,2.1237905025482178 9.412992477416992,2.1370036602020264 9.298343658447266,2.1530466079711914 9.184666633605957,2.1718804836273193 9.071986198425293,2.1934690475463867 8.960328102111816,2.2177765369415283 8.849720001220703,2.244767904281616 8.740190505981445,2.2744064331054688 8.631766319274902,2.30665922164917 8.524478912353516,2.3414909839630127 8.41835880279541,2.3788681030273438 8.313438415527344,2.4187560081481934 8.209748268127441,2.461121082305908 8.10732364654541,2.5059285163879395 8.00619888305664,2.553144693374634 7.906407356262207,2.602734327316284 7.807985305786133,2.6546630859375 7.710968971252441,2.708897590637207 7.615395545959473,2.7654008865356445 7.52130126953125,2.824138641357422 7.4287238121032715,2.8850746154785156 7.337701320648193,2.9481723308563232 7.24827241897583,3.0133965015411377 7.1604743003845215,3.08070969581604 7.074346542358398,3.1500749588012695 6.989927768707275,3.2214558124542236 6.907256126403809,3.2948131561279297 6.8263702392578125,3.370110273361206 6.747308731079102,3.4473087787628174 6.67011022567749,3.526370048522949 6.594813346862793,3.607255697250366 6.521455764770508,3.689927577972412 6.450075626373291,3.7743465900421143 6.380710124969482,3.8604745864868164 6.313396453857422,3.9482719898223877 6.248172760009766,4.03770112991333 6.185074329376221,4.128723621368408 6.124138832092285,4.221301078796387 6.065401077270508,4.315395355224609 6.00889778137207,4.410968780517578 5.9546637535095215,4.5079851150512695 5.902734279632568,4.606407165527344 5.853144645690918,4.706198215484619 5.805928707122803,4.807323932647705 5.7611212730407715,4.909748554229736 5.718756675720215,5.013437747955322 5.678868293762207,5.118359088897705 5.641491413116455,5.224478721618652 5.606659412384033,5.331766128540039 5.574406623840332,5.440189838409424 5.544767379760742,5.549720287322998 5.517776966094971,5.660327911376953 5.49346923828125,5.77198600769043 5.4718804359436035,5.884666442871094 5.453046798706055,5.998343467712402 5.437004089355469,6.112992286682129 5.423790454864502,6.228589057922363 5.413443565368652,6.345109939575195 5.406002521514893,6.462532997131348 5.401507377624512,6.580836772918701 5.400000095367432,6.699999809265137 5.400000095367432,14.699999809265137 5.401507377624512,14.81916332244873 5.406002521514893,14.937466621398926 5.413443565368652,15.054889678955078 5.423790454864502,15.17141056060791 5.437004089355469,15.287007331848145 5.453046798706055,15.401656150817871 5.4718804359436035,15.51533317565918 5.49346923828125,15.628013610839844 5.517776966094971,15.73967170715332 5.544767379760742,15.850279808044434 5.574406623840332,15.959810256958008 5.606659412384033,16.068233489990234 5.641491413116455,16.175519943237305 5.678868293762207,16.281641006469727 5.699999809265137,16.33722496032715 5.699999809265137,17 5.702534198760986,17.200210571289062 5.710105895996094,17.39946746826172 5.7226667404174805,17.597671508789062 5.740165710449219,17.79472541809082 5.762548446655273,17.99053192138672 5.789760112762451,18.184995651245117 5.821741580963135,18.378026962280273 5.858433246612549,18.569534301757812 5.899773597717285,18.759431838989258 5.945699214935303,18.947633743286133 5.996146202087402,19.13405990600586 6.05104923248291,19.318626403808594 6.1103434562683105,19.50126075744629 6.173961639404297,19.681884765625 6.241838455200195,19.860427856445312 6.253403186798096,19.88873291015625 6.170251846313477,19.828086853027344 6.048267841339111,19.73456382751465 5.92863130569458,19.63821029663086 5.811390399932861,19.539077758789062 5.696592330932617,19.437213897705078 5.584284782409668,19.332666397094727 5.47451639175415,19.225482940673828 5.367334365844727,19.11571502685547 5.262786865234375,19.003408432006836 5.160922527313232,18.888608932495117 5.061789512634277,18.7713680267334 4.965436935424805,18.651731491088867 4.871913433074951,18.529747009277344 4.781269073486328,18.40546417236328 4.693552494049072,18.278928756713867 4.608814716339111,18.150190353393555 4.527105808258057,18.01929473876953 4.4484758377075195,17.886295318603516 4.372976779937744,17.751237869262695 4.3006591796875,17.614173889160156 4.231574535369873,17.47515296936035 4.165775299072266,17.334226608276367 4.103312969207764,17.191444396972656 4.0442399978637695,17.04686164855957 3.9886083602905273,16.900527954101562 3.9364702701568604,16.752498626708984 3.8878791332244873,16.602828979492188 3.8428850173950195,16.451574325561523 3.8015410900115967,16.298791885375977 3.763899803161621,16.1445369720459 3.730011463165283,15.988869667053223 3.699927568435669,15.831849098205566 3.6736979484558105,15.673535346984863 3.6513729095458984,15.513989448547363 3.6330018043518066,15.353275299072266 3.618631601333618,15.191454887390137 3.6083109378814697,15.028592109680176 3.6020851135253906,14.864751815795898 3.5999996662139893,14.699999809265137 3.5999996662139893,6.699999809265137 3.6020851135253906,6.535247802734375 3.6083109378814697,6.371407985687256 3.618631601333618,6.208545207977295 3.6330018043518066,6.046724319458008 3.6513729095458984,5.886009693145752 3.6736979484558105,5.726464748382568 3.699927568435669,5.568150997161865 3.730011463165283,5.411129951477051 3.763899803161621,5.255462646484375 3.8015410900115967,5.101208209991455 3.8428850173950195,4.948424816131592 3.8878791332244873,4.7971696853637695 3.9364707469940186,4.647500514984131 3.9886083602905273,4.499471664428711 4.0442399978637695,4.353138446807861 4.103313446044922,4.208554744720459 4.165775299072266,4.065773010253906 4.231575012207031,3.9248461723327637 4.3006591796875,3.785825252532959 4.372976779937744,3.64876127243042 4.4484758377075195,3.513704299926758 4.527105808258057,3.380703926086426 4.608814716339111,3.249810218811035 4.6935529708862305,3.1210711002349854 4.781269073486328,2.9945361614227295 4.871913909912109,2.8702521324157715 4.965436935424805,2.7482681274414062 5.0617899894714355,2.628631830215454 5.160922527313232,2.511390447616577 5.262787342071533,2.396592140197754 5.367334365844727,2.284285068511963 5.47451639175415,2.174516201019287 5.584285259246826,2.0673344135284424 5.696592330932617,1.96278715133667 5.811390399932861,1.8609228134155273 5.928631782531738,1.7617894411087036 6.0482683181762695,1.6654369831085205 6.170252323150635,1.5719135999679565 6.294536113739014,1.4812688827514648 6.421071529388428,1.3935524225234985 6.549810409545898,1.308814525604248 6.680704593658447,1.2271054983139038 6.813704490661621,1.1484757661819458 6.948761463165283,1.07297682762146 7.085825443267822,1.0006589889526367 7.224846363067627,0.9315745234489441 7.3657732009887695,0.8657752871513367 7.508554935455322,0.8033130168914795 7.653138637542725,0.7442401647567749 7.799471855163574,0.6886082291603088 7.947500705718994,0.6364702582359314 8.097169876098633,0.5878788232803345 8.248424530029297,0.5428849458694458 8.40120792388916,0.501541256904602 8.555462837219238,0.4638996720314026 8.711130142211914,0.4300115704536438 8.86815071105957,0.39992740750312805 9.026464462280273,0.3736977279186249 9.186010360717773,0.3513730466365814 9.346724510192871,0.33300116658210754 9.508544921875,0.31863120198249817 9.671407699584961,0.3083103597164154 9.835247993469238,0.3020848333835602 10,0.29999983310699463z";

    // Modifier keycaps for the left-edge mod bar. Keycap = rounded rect x8..16 over y4..7 with
    // the modifier's mark below it. The latched variants add a rim circling the glyph (primary
    // cue) and a filled dot in the right margin (secondary cue).
    private const string Keycap = "M8.2,7.2 L8.2,5.2 A1.2,1.2 0 0 1 9.4,4 L14.6,4 A1.2,1.2 0 0 1 15.8,5.2 L15.8,7.2";
    private const string ShiftMark = " M6.5,9.5 L12,4.6 L17.5,9.5 M12,4.6 L12,19";
    private const string CtrlMark = " M9,10.5 L12,7.8 L15,10.5 M12,13 L12,19 M8,16.5 L16,16.5";
    private const string AltMark = " M8.5,10.5 L10.5,12.4 M15.5,10.5 L13.5,12.4 M10.5,12.4 A3,3 0 1 0 13.5,12.4 M12,15.4 L12,19";

    /// <summary>
    /// The rim the latched keycap variants draw around the whole glyph. Internal because the
    /// registry test pins that the latched STROKE geometry contains it — the rim is the primary
    /// latched cue (outline-level, the peripheral-vision rule), the filled dot is secondary.
    /// Written in the path mini-language's canonical serialization (comma separators, one figure
    /// per arc) because the test asserts it against <see cref="Geometry.ToString"/>, which
    /// re-serializes rather than echoing the authored text. Integer-only values keep it
    /// parseable under every culture: the parser treats "." decimals and "," separators the same
    /// way regardless of the thread's culture, but a decimal mark would serialize as "," on a
    /// comma-decimal culture and break the substring match there.
    /// </summary>
    internal const string LatchedRim = " M4,8A2,2,0,0,1,6,6 M20,6A2,2,0,0,1,22,8 M22,16A2,2,0,0,1,20,18 M6,18A2,2,0,0,1,4,16";
    private const string LatchedDot = "M19.9,2.1 A1.9,1.9 0 1 1 20,5.9 A1.9,1.9 0 1 1 19.9,2.1 Z";

    private static readonly Dictionary<ActionIcon, IconGeometry> Map = Build();

    /// <summary>The geometry for <paramref name="icon"/>. Throws if the registry has no entry.</summary>
    /// <param name="icon">Which icon to resolve.</param>
    /// <returns>That icon's two layers.</returns>
    public static IconGeometry For(ActionIcon icon) => Map[icon];

    private static Dictionary<ActionIcon, IconGeometry> Build() => new()
    {
        // The left-pressed member of the "popped" family: the solid cap lifts out up-left and the
        // socket stays open, so the pressed side is readable from the silhouette alone.
        [ActionIcon.LeftClick] = IconGeometry.FromPathData(PoppedLeftBody, PoppedLeftFill),

        [ActionIcon.RightClick] = IconGeometry.FromPathData(PoppedRightBody, PoppedRightFill),

        // The middle variant: the wheel pillar rises through the socket the body leaves, the same
        // "the pressed part sticks out" language as the two click icons — the wheel cannot look
        // like the plain mouse this way.
        [ActionIcon.MiddleClick] = IconGeometry.FromPathData(PoppedMiddleBody, PoppedWheelFill),

        // Two mice, the back one peeking out up-left: two clicks drawn as two of the thing. The
        // outline changes shape rather than gaining a mark inside it, per the class doc.
        [ActionIcon.DoubleClick] = IconGeometry.FromPathData(TwoMiceStroke, TwoMiceFill),

        // The held left button (the popped family's left cap) plus motion bars trailing at the
        // left. The body follows the family so the drag reads as the same mouse as LeftClick —
        // only the bars say "held and moving". The bars sit clear of the body's wall: at x3.6 and
        // x1 the stroked bars span 2.7–4.5, a ~0.6-unit gap to the wall's 5.1–6.9, so in the ring
        // the pair reads as motion rather than a thickened wall. The upper bar's round cap ends
        // just below the lifted cap (y10.5), never crossing it, and x1 keeps the ink inside the
        // grid at every size the fit tests sweep.
        [ActionIcon.Drag] = IconGeometry.FromPathData(
            PoppedLeftBody + " M3.6,11.6 L3.6,15.4 M1,10.6 L1,13.4",
            PoppedLeftFill),

        [ActionIcon.ScrollUp] = IconGeometry.FromPathData(Body + Wheel + " M18.6,12.4 L20.8,10 L23,12.4"),

        [ActionIcon.ScrollDown] = IconGeometry.FromPathData(Body + Wheel + " M18.6,11.6 L20.8,14 L23,11.6"),

        [ActionIcon.ScrollLeft] = IconGeometry.FromPathData(Body + Wheel + " M22,9.4 L19.6,11.8 L22,14.2"),

        [ActionIcon.ScrollRight] = IconGeometry.FromPathData(Body + Wheel + " M19.6,9.4 L22,11.8 L19.6,14.2"),

        // Both chevrons: the gaze-joystick mode that scrolls either way.
        [ActionIcon.ScrollMode] = IconGeometry.FromPathData(
            Body + Wheel + " M18.6,9.4 L20.8,7 L23,9.4 M18.6,14.6 L20.8,17 L23,14.6"),

        // A note with two text lines, and a pin whose head is the only filled part.
        [ActionIcon.PinNote] = IconGeometry.FromPathData(
            "M5,10 L5,19 A2.5,2.5 0 0 0 7.5,21.5 L16.5,21.5 A2.5,2.5 0 0 0 19,19 L19,10"
            + " A2.5,2.5 0 0 0 16.5,7.5 L7.5,7.5 A2.5,2.5 0 0 0 5,10 Z"
            + " M8.6,12.5 L15.4,12.5 M8.6,16.5 L13,16.5 M12,5.8 L12,7.5",
            "M9.8,3.6 A2.2,2.2 0 1 1 14.2,3.6 A2.2,2.2 0 1 1 9.8,3.6 Z"),

        // A keyboard: rounded body, one dash row, a second short row, and a centred space bar.
        // The first non-mouse ACTION icon — see the revised class doc above.
        [ActionIcon.Keyboard] = IconGeometry.FromPathData(
            "M6,7.5 L18,7.5 A2.5,2.5 0 0 1 20.5,10 L20.5,15 A2.5,2.5 0 0 1 18,17.5 L6,17.5 A2.5,2.5 0 0 1 3.5,15 L3.5,10 A2.5,2.5 0 0 1 6,7.5 Z"
            + " M6.8,10.7 L9.2,10.7 M10.7,10.7 L13.3,10.7 M14.8,10.7 L17.2,10.7"
            + " M6.8,13.2 L9.2,13.2 M14.8,13.2 L17.2,13.2"
            + " M9.7,15.7 L14.3,15.7"),

        // A compact keyboard with chevrons above and below: the edge bar's flip-to-other-edge
        // affordance. Chevrons change the outline from the plain Keyboard glyph, per the
        // peripheral-vision rule in the class doc.
        [ActionIcon.KeyboardEdge] = IconGeometry.FromPathData(
            "M7,10.2 L16.2,10.2 A2,2 0 0 1 18.2,12.2 L18.2,14.2 A2,2 0 0 1 16.2,16.2 L7,16.2 A2,2 0 0 1 5,14.2 L5,12.2 A2,2 0 0 1 7,10.2 Z"
            + " M7.8,12 L9.8,12 M10.8,12 L12.8,12 M13.8,12 L15.4,12"
            + " M7.8,14.4 L15.4,14.4"
            + " M9.6,7.4 L11.6,5.4 L13.6,7.4 M9.6,19 L11.6,21 L13.6,19"),

        // A camera with a clockwise arrow (user pick Re3, 2026-10-06): the camera body and its
        // lens ring are stroked, the arrowhead where the ring opens is filled. It replaces the
        // ↻ glyph and its drawn rotation arrow, and stays the one action icon that is not a mouse
        // — it re-shoots the screen region, not a click.
        [ActionIcon.Recapture] = IconGeometry.FromPathData(
            "M5,8 L8.2,8 L9.6,5.4 L14.4,5.4 L15.8,8 L19,8 A2.5,2.5 0 0 1 21.5,10.5 L21.5,18.5"
            + " A2.5,2.5 0 0 1 19,21 L5,21 A2.5,2.5 0 0 1 2.5,18.5 L2.5,10.5 A2.5,2.5 0 0 1 5,8 Z"
            + " M15.57,13.14 A3.7,3.7 0 1 1 12.64,10.46 M12.99,8.49 L15.01,10.87 L12.3,12.43 Z",
            "M12.99,8.49 L15.01,10.87 L12.3,12.43 Z"),

        [ActionIcon.Back] = IconGeometry.FromPathData(
            "M10,5.5 L4.5,11 L10,16.5 M4.5,11 L14.5,11 A5,5 0 0 1 19.5,16 L19.5,19"),

        // Fill-only: three dots. More is one of the set's three non-mouse icons (see the class
        // doc): navigation, and drawing it as a mouse would say it performs a click.
        [ActionIcon.More] = IconGeometry.FromPathData(null,
            "M3.7,12 A1.9,1.9 0 1 1 7.5,12 A1.9,1.9 0 1 1 3.7,12 Z"
            + " M10.1,12 A1.9,1.9 0 1 1 13.9,12 A1.9,1.9 0 1 1 10.1,12 Z"
            + " M16.5,12 A1.9,1.9 0 1 1 20.3,12 A1.9,1.9 0 1 1 16.5,12 Z"),

        // A solid X with a lighter 2.26-unit arm (live-check revision, 2026-10-07): the same
        // construction as the user's C2 pick, whose 3.2-unit arm read as too bold on the real
        // edge bar. The one target that must read as "none of the above", so it borrows no part
        // of the mouse silhouette the actions share. Fill-only, like More.
        [ActionIcon.Close] = IconGeometry.FromPathData(
            null,
            "M6.33,4.73 L12,10.4 L17.67,4.73 A1.13,1.13 0 0 1 19.27,6.33 L13.6,12 L19.27,17.67"
            + " A1.13,1.13 0 0 1 17.67,19.27 L12,13.6 L6.33,19.27 A1.13,1.13 0 0 1 4.73,17.67"
            + " L10.4,12 L4.73,6.33 A1.13,1.13 0 0 1 6.33,4.73 Z"),

        // The bold question mark (user pick H1): a 4.2-radius hook with a low stem, and the dot —
        // the only filled part. Bigger and rounder than the old hook so it holds at 40px.
        [ActionIcon.Help] = IconGeometry.FromPathData(
            "M7.8,8.3 A4.2,4.2 0 1 1 13.44,12.25 C11.55,12.88 12,13.93 12,14.6 L12,15.6",
            "M10.4,19.5 A1.6,1.6 0 1 1 13.6,19.5 A1.6,1.6 0 1 1 10.4,19.5 Z"),

        // Modifier keycaps for the left-edge mod bar (24-grid; the latched variants' PRIMARY cue
        // is the rim drawn into the stroke geometry, the filled dot at (20, 4) in the right margin
        // is secondary — the peripheral-vision rule is why the dot alone is not enough).
        [ActionIcon.Shift] = IconGeometry.FromPathData(Keycap + ShiftMark),
        [ActionIcon.Ctrl] = IconGeometry.FromPathData(Keycap + CtrlMark),
        [ActionIcon.Alt] = IconGeometry.FromPathData(Keycap + AltMark),
        [ActionIcon.ShiftLatched] = IconGeometry.FromPathData(Keycap + ShiftMark + LatchedRim, LatchedDot),
        [ActionIcon.CtrlLatched] = IconGeometry.FromPathData(Keycap + CtrlMark + LatchedRim, LatchedDot),
        [ActionIcon.AltLatched] = IconGeometry.FromPathData(Keycap + AltMark + LatchedRim, LatchedDot),
    };
}
