namespace EyeCtrl.Core.Prediction;

/// <summary>
/// Maps each key character to its physically adjacent neighbours on a given keyboard layout.
/// Used by FuzzyEditDistance to apply a reduced substitution cost (0.5) for adjacent-key errors.
/// </summary>
public static class KeyboardAdjacencyMap
{
    private static readonly Dictionary<char, HashSet<char>> Qwerty = new()
    {
        ['q'] = ['w','a'],
        ['w'] = ['q','e','a','s'],
        ['e'] = ['w','r','s','d'],
        ['r'] = ['e','t','d','f'],
        ['t'] = ['r','y','f','g'],
        ['y'] = ['t','u','g','h'],
        ['u'] = ['y','i','h','j'],
        ['i'] = ['u','o','j','k'],
        ['o'] = ['i','p','k','l'],
        ['p'] = ['o','l'],
        ['a'] = ['q','w','s','z'],
        ['s'] = ['a','w','e','d','z','x'],
        ['d'] = ['s','e','r','f','x','c'],
        ['f'] = ['d','r','t','g','c','v'],
        ['g'] = ['f','t','y','h','v','b'],
        ['h'] = ['g','y','u','j','b','n'],
        ['j'] = ['h','u','i','k','n','m'],
        ['k'] = ['j','i','o','l','m'],
        ['l'] = ['k','o','p'],
        ['z'] = ['a','s','x'],
        ['x'] = ['z','s','d','c'],
        ['c'] = ['x','d','f','v'],
        ['v'] = ['c','f','g','b'],
        ['b'] = ['v','g','h','n'],
        ['n'] = ['b','h','j','m'],
        ['m'] = ['n','j','k'],
    };

    private static readonly Dictionary<char, HashSet<char>> Qwertz = new()
    {
        ['q'] = ['w','a'],
        ['w'] = ['q','e','a','s'],
        ['e'] = ['w','r','s','d'],
        ['r'] = ['e','t','d','f'],
        ['t'] = ['r','z','f','g'],
        ['z'] = ['t','u','g','h'],
        ['u'] = ['z','i','h','j'],
        ['i'] = ['u','o','j','k'],
        ['o'] = ['i','p','k','l'],
        ['p'] = ['o','l'],
        ['a'] = ['q','w','s','y'],
        ['s'] = ['a','w','e','d','y','x'],
        ['d'] = ['s','e','r','f','x','c'],
        ['f'] = ['d','r','t','g','c','v'],
        ['g'] = ['f','t','z','h','v','b'],
        ['h'] = ['g','z','u','j','b','n'],
        ['j'] = ['h','u','i','k','n','m'],
        ['k'] = ['j','i','o','l','m'],
        ['l'] = ['k','o','p'],
        ['y'] = ['a','s','x'],
        ['x'] = ['y','s','d','c'],
        ['c'] = ['x','d','f','v'],
        ['v'] = ['c','f','g','b'],
        ['b'] = ['v','g','h','n'],
        ['n'] = ['b','h','j','m'],
        ['m'] = ['n','j','k'],
    };

    private static readonly Dictionary<char, HashSet<char>> Azerty = new()
    {
        ['a'] = ['z','q'],
        ['z'] = ['a','e','q','s'],
        ['e'] = ['z','r','s','d'],
        ['r'] = ['e','t','d','f'],
        ['t'] = ['r','y','f','g'],
        ['y'] = ['t','u','g','h'],
        ['u'] = ['y','i','h','j'],
        ['i'] = ['u','o','j','k'],
        ['o'] = ['i','p','k','l'],
        ['p'] = ['o','l','m'],
        ['q'] = ['a','z','s','w'],
        ['s'] = ['q','z','e','d','w','x'],
        ['d'] = ['s','e','r','f','x','c'],
        ['f'] = ['d','r','t','g','c','v'],
        ['g'] = ['f','t','y','h','v','b'],
        ['h'] = ['g','y','u','j','b','n'],
        ['j'] = ['h','u','i','k','n','m'],
        ['k'] = ['j','i','o','l','m'],
        ['l'] = ['k','o','p','m'],
        ['m'] = ['l','p'],
        ['w'] = ['q','s','x'],
        ['x'] = ['w','s','d','c'],
        ['c'] = ['x','d','f','v'],
        ['v'] = ['c','f','g','b'],
        ['b'] = ['v','g','h','n'],
        ['n'] = ['b','h','j','m'],
    };

    public static Dictionary<char, HashSet<char>> For(string layoutName) =>
        layoutName.ToUpperInvariant() switch
        {
            "QWERTZ" => Qwertz,
            "AZERTY" => Azerty,
            _        => Qwerty,
        };

    public static bool AreAdjacent(Dictionary<char, HashSet<char>> map, char a, char b) =>
        map.TryGetValue(a, out var neighbours) && neighbours.Contains(b);
}
