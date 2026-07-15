using System;

namespace Optimisation_Tool.Helpers
{
    public static class FeedbackMessageClassifier
    {
        public static bool IsFailure(string message)
        {
            string value = message.Trim().ToLowerInvariant();
            return value.StartsWith("erreur", StringComparison.Ordinal) ||
                   value.Contains(": erreur", StringComparison.Ordinal) ||
                   value.Contains("— erreur", StringComparison.Ordinal) ||
                   value.Contains("échoué", StringComparison.Ordinal) ||
                   value.Contains("echec", StringComparison.Ordinal) ||
                   value.Contains("échec", StringComparison.Ordinal) ||
                   value.Contains("refusé", StringComparison.Ordinal) ||
                   value.Contains("impossible", StringComparison.Ordinal) ||
                   value.Contains("n'a pas été appliqué", StringComparison.Ordinal) ||
                   value.Contains("non appliqué", StringComparison.Ordinal) ||
                   value.Contains("restauration incomplète", StringComparison.Ordinal);
        }
    }
}
