using System;
using System.Collections;

namespace SmartLock
{
    class Utils
    {
        public static void ArrayListCopy(ArrayList from, ArrayList to)
        {
            to.Clear();
            foreach (Object elem in from)
            {
                to.Add(elem);
            }
        }
    }
}
