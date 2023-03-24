using System.Collections.Generic;

namespace Mumrich.SpaDevMiddleware.MsBuild.Extensions
{
  public static class DictionaryExtensions
  {
    public static bool TryAdd<TKey, TValue>(
      this Dictionary<TKey, TValue> dictionary,
      TKey key,
      TValue value)
    {
      bool response = false;

      if (dictionary?.ContainsKey(key) == false)
      {
        dictionary.Add(key, value);
        response = true;
      }

      return response;
    }
  }
}