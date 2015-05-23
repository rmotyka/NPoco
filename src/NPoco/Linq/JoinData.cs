using System;
using System.Collections.Generic;
using System.Reflection;

namespace NPoco.Linq
{
    public class JoinData
    {
        public string OnSql { get; set; }
        public PocoMember PocoMember { get; set; }
        public List<PocoMember> PocoMembers { get; set; }
    }
}