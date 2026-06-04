using System.Collections.Generic;

namespace MediaInfoKeeper.Common
{
    public static class UnionFindUtility
    {
        public static long Find(long x, Dictionary<long, long> parent)
        {
            if (!parent.TryGetValue(x, out var p))
            {
                parent[x] = x;
                return x;
            }

            if (p != x)
            {
                parent[x] = Find(p, parent);
            }

            return parent[x];
        }

        public static void Union(long x, long y, Dictionary<long, long> parent)
        {
            var rootX = Find(x, parent);
            var rootY = Find(y, parent);

            if (rootX != rootY)
            {
                parent[rootX] = rootY;
            }
        }
    }
}
