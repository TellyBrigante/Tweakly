using System;

namespace Optimisation_Tool.Helpers
{
    /// <summary>
    /// Resultat d'une lecture systeme. La valeur de repli n'est jamais consideree
    /// comme une mesure valide lorsque Success vaut false.
    /// </summary>
    public readonly record struct ProbeResult<T>(bool Success, T Value, string Error)
    {
        public static ProbeResult<T> Capture(string context, Func<T> read, T fallback)
        {
            try
            {
                return Available(read());
            }
            catch (Exception ex)
            {
                AppLog.Error(context, ex);
                return Unavailable(fallback, ex.Message);
            }
        }

        public static ProbeResult<T> FromTry(
            string context,
            bool success,
            T value,
            string error,
            T fallback)
        {
            if (success)
                return Available(value);

            string detail = string.IsNullOrWhiteSpace(error)
                ? "lecture indisponible"
                : error.Trim();
            AppLog.Write($"{context} : {detail}");
            return Unavailable(fallback, detail);
        }

        public static ProbeResult<T> Available(T value)
            => new(true, value, "");

        public static ProbeResult<T> Unavailable(T fallback, string error)
            => new(false, fallback, error);
    }
}
