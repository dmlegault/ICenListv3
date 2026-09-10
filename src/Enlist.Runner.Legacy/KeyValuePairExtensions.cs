namespace System.Collections.Generic
{
    /// <summary>
    /// netcoreapp allows `foreach (var (key, value) in dictionary)` because KeyValuePair itself ships a
    /// Deconstruct method there; net472's KeyValuePair does not, so this extension method supplies it.
    /// Extension deconstruction is resolved by the compiler the same way any other extension method is
    /// (by name + shape, found via a using or — as here — same-namespace visibility), so this is a
    /// legitimate polyfill, not a language-version workaround like IsExternalInit.cs's marker types.
    /// </summary>
    internal static class KeyValuePairExtensions
    {
        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }
    }
}
