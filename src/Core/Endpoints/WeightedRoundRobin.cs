using System;
using System.Collections.Generic;

namespace AspNetCore.Proxy.Endpoints
{
    /// <summary>
    /// Weighted round robin endpoint helpers.
    /// </summary>
    public static class WeightedRoundRobin
    {
        /// <summary>
        /// A helper method that selects endpoints using a weighted round robin algorithm.
        /// Endpoints with higher weights receive proportionally more requests.
        /// </summary>
        /// <param name="weightedEndpoints">
        /// The set of endpoints with their weights, e.g.
        /// <c>("http://server1", 3), ("http://server2", 1)</c>
        /// routes 3/4 of requests to server1 and 1/4 to server2.
        /// </param>
        /// <returns>An <see cref="EndpointComputerToString"/> that distributes requests by weight.</returns>
        public static EndpointComputerToString Of(params (string endpoint, int weight)[] weightedEndpoints)
        {
            if (weightedEndpoints == null || weightedEndpoints.Length == 0)
                throw new ArgumentException("At least one weighted endpoint must be provided.", nameof(weightedEndpoints));

            var expanded = new List<string>();
            foreach (var (endpoint, weight) in weightedEndpoints)
            {
                if (weight <= 0)
                    throw new ArgumentException($"Weight for endpoint '{endpoint}' must be greater than zero.", nameof(weightedEndpoints));
                for (var i = 0; i < weight; i++)
                    expanded.Add(endpoint);
            }

            var position = 0;
            var pool = expanded.ToArray();

            return (context, args) =>
            {
                if (position >= pool.Length)
                    position = 0;
                return pool[position++];
            };
        }
    }
}
