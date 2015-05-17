using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace NPoco.FluentMappings
{
    public class FluentConfig
    {
        public FluentConfig(Func<IMapper, PocoDataFactory> config)
        {
            Config = config;
        }

        public Func<IMapper, PocoDataFactory> Config { get; private set; }
    }

    public class FluentMappingConfiguration
    {
        public static FluentConfig Configure(params IMap[] pocoMaps)
        {
            var mappings = Mappings.BuildMappingsFromMaps(pocoMaps);
            return Configure(mappings);
        }

        public static FluentConfig Configure(Mappings mappings)
        {
            return SetFactory(mappings, null);
        }

        public static FluentConfig Scan(Action<IConventionScanner> scanner)
        {
            var scannerSettings = ProcessSettings(scanner);
            if (scannerSettings.Lazy)
            {
                var lazyPocoMappings = new Mappings();
                return SetFactory(lazyPocoMappings, scanner);
            }
            
            return Configure(CreateMappings(scannerSettings, null));
        }

        private static Mappings CreateMappings(ConventionScannerSettings scannerSettings, Type[] typesOverride)
        {
            var types = typesOverride ?? FindTypes(scannerSettings);
            var config = new Dictionary<Type, TypeDefinition>();
            
            foreach (var type in types)
            {
                var pocoDefn = new TypeDefinition(type)
                {
                    AutoIncrement = scannerSettings.PrimaryKeysAutoIncremented(type),
                    PrimaryKey = scannerSettings.PrimaryKeysNamed(type),
                    TableName = scannerSettings.TablesNamed(type),
                    SequenceName = scannerSettings.SequencesNamed(type),
                    ExplicitColumns = true
                };

                foreach (var columnDefinition in GetColumnDefinitions(scannerSettings, type, new List<MemberInfo>()))
                {
                    var key = string.Join("__", columnDefinition.MemberInfoChain.Select(x => x.Name));
                    if (!pocoDefn.ColumnConfiguration.ContainsKey(key))
                        pocoDefn.ColumnConfiguration.Add(key, columnDefinition);
                }
                
                config.Add(type, pocoDefn);
            }

            MergeOverrides(config, scannerSettings.MappingOverrides);

            //if (scannerSettings.OverrideWithAttributes)
            //{
            //    MergeAttributeOverrides(config);
            //}

            var pocoMappings = new Mappings {Config = config};
            return pocoMappings;
        }

        private static IEnumerable<ColumnDefinition> GetColumnDefinitions(ConventionScannerSettings scannerSettings, Type type, List<MemberInfo> memberInfos, bool isReferenceProperty = false)
        {
            var capturedMembers = memberInfos.ToArray();
            foreach (var member in ReflectionUtils.GetFieldsAndPropertiesForClasses(type))
            {
                var complexProperty = scannerSettings.ComplexPropertiesWhere(member);
                var referenceProperty = scannerSettings.ReferencePropertiesWhere(member);
                if (complexProperty || referenceProperty)
                {
                    var members = new List<MemberInfo>();
                    members.AddRange(capturedMembers);
                    members.Add(member);
                    foreach (var columnDefinition in GetColumnDefinitions(scannerSettings, member.GetMemberInfoType(), members, referenceProperty))
                    {
                        yield return columnDefinition;
                    }

                    yield return new ColumnDefinition()
                    {
                        MemberInfoChain = capturedMembers.Concat(new[] {member}).ToArray(),
                        MemberInfo = member,
                        IsComplexMapping = complexProperty,
                        IsReferenceMember = referenceProperty,
                        DbColumnName = referenceProperty ? scannerSettings.ReferenceDbColumnsNamed(member) : null
                    };
                }
                else
                {
                    var columnDefinition = new ColumnDefinition();
                    columnDefinition.MemberInfoChain = capturedMembers.Concat(new[] {member}).ToArray();
                    columnDefinition.MemberInfo = member;

                    var prefixProperty = isReferenceProperty ? Enumerable.Empty<string>() : capturedMembers.Select(x => scannerSettings.DbColumnsNamed(x));
                    columnDefinition.DbColumnName = string.Join("__", prefixProperty.Concat(new[] { scannerSettings.DbColumnsNamed(member) }));
                    
                    columnDefinition.DbColumnAlias = scannerSettings.AliasNamed(member);
                    columnDefinition.IgnoreColumn = scannerSettings.IgnorePropertiesWhere.Any(x => x.Invoke(member));
                    columnDefinition.DbColumnType = scannerSettings.DbColumnTypesAs(member);
                    columnDefinition.ResultColumn = scannerSettings.ResultPropertiesWhere(member);
                    columnDefinition.ComputedColumn = scannerSettings.ComputedPropertiesWhere(member);
                    columnDefinition.VersionColumn = scannerSettings.VersionPropertiesWhere(member);
                    columnDefinition.VersionColumnType = scannerSettings.VersionColumnTypeAs(member);
                    columnDefinition.ForceUtc = scannerSettings.ForceDateTimesToUtcWhere(member);
                    yield return columnDefinition;
                }
            }
        }

        private static ConventionScannerSettings ProcessSettings(Action<IConventionScanner> scanner)
        {
            var defaultScannerSettings = new ConventionScannerSettings
            {
                PrimaryKeysAutoIncremented = x => true,
                PrimaryKeysNamed = x => "ID",
                TablesNamed = x => x.Name,
                DbColumnsNamed = x => x.Name,
                AliasNamed = x => null,
                DbColumnTypesAs = x => null,
                ResultPropertiesWhere = x => false,
                VersionPropertiesWhere = x => false,
                VersionColumnTypeAs = x => VersionColumnType.Number,
                ComputedPropertiesWhere = x => false,
                ForceDateTimesToUtcWhere = x => true,
                ReferencePropertiesWhere = x => x.GetMemberInfoType().IsAClass() && Attribute.GetCustomAttributes(x, typeof(ReferenceAttribute)).Any(),
                ComplexPropertiesWhere = x => x.GetMemberInfoType().IsAClass() && !Attribute.GetCustomAttributes(x, typeof(ReferenceAttribute)).Any(),
                ReferenceDbColumnsNamed = x => x.Name + "ID",
                SequencesNamed = x => null,
                Lazy = false
            };
            scanner.Invoke(new ConventionScanner(defaultScannerSettings));
            return defaultScannerSettings;
        }

        private static IEnumerable<Type> FindTypes(ConventionScannerSettings scannerSettings)
        {
            if (scannerSettings.TheCallingAssembly)
                scannerSettings.Assemblies.Add(FindTheCallingAssembly());

            var types = scannerSettings.Assemblies
                .SelectMany(x => x.GetExportedTypes())
                .Where(x => scannerSettings.IncludeTypes.All(y => y.Invoke(x)))
                .Where(x => !x.IsNested && !typeof (Map<>).IsAssignableFrom(x) && !typeof (Mappings).IsAssignableFrom(x));
            return types;
        }

        private static void MergeAttributeOverrides(Dictionary<Type, TypeDefinition> config)
        {
            foreach (var typeDefinition in config)
            {
                var tableInfo = TableInfo.FromPoco(typeDefinition.Key);
                typeDefinition.Value.TableName = tableInfo.TableName;
                typeDefinition.Value.PrimaryKey = tableInfo.PrimaryKey;
                typeDefinition.Value.SequenceName = tableInfo.SequenceName;
                typeDefinition.Value.AutoIncrement = tableInfo.AutoIncrement;

                foreach (var columnDefinition in typeDefinition.Value.ColumnConfiguration)
                {
                    var columnInfo = ColumnInfo.FromMemberInfo(columnDefinition.Value.MemberInfo);
                    columnDefinition.Value.DbColumnName = columnInfo.ColumnName;
                    columnDefinition.Value.DbColumnAlias = columnInfo.ColumnAlias;
                    columnDefinition.Value.DbColumnType = columnInfo.ColumnType;
                    columnDefinition.Value.IgnoreColumn = columnInfo.IgnoreColumn;
                    columnDefinition.Value.ResultColumn = columnInfo.ResultColumn;
                    columnDefinition.Value.ComputedColumn = columnInfo.ComputedColumn;
                    columnDefinition.Value.VersionColumn = columnInfo.VersionColumn;
                    columnDefinition.Value.VersionColumnType = columnInfo.VersionColumnType;
                    columnDefinition.Value.ForceUtc = columnInfo.ForceToUtc;
                }
            }
        }

        private static void MergeOverrides(Dictionary<Type, TypeDefinition> config, Mappings overrideMappings)
        {
            if (overrideMappings == null)
                return;

            foreach (var overrideTypeDefinition in overrideMappings.Config)
            {
                if (!config.ContainsKey(overrideTypeDefinition.Key))
                    continue;

                var convTableDefinition = config[overrideTypeDefinition.Key];

                convTableDefinition.PrimaryKey = overrideTypeDefinition.Value.PrimaryKey ?? convTableDefinition.PrimaryKey;
                convTableDefinition.SequenceName = overrideTypeDefinition.Value.SequenceName ?? convTableDefinition.SequenceName;
                convTableDefinition.TableName = overrideTypeDefinition.Value.TableName ?? convTableDefinition.TableName;
                convTableDefinition.AutoIncrement = overrideTypeDefinition.Value.AutoIncrement ?? convTableDefinition.AutoIncrement;
                convTableDefinition.ExplicitColumns = overrideTypeDefinition.Value.ExplicitColumns ?? convTableDefinition.ExplicitColumns;

                foreach (var overrideColumnDefinition in overrideMappings.Config[overrideTypeDefinition.Key].ColumnConfiguration)
                {
                    var convColDefinition = convTableDefinition.ColumnConfiguration[overrideColumnDefinition.Key];

                    convColDefinition.DbColumnName = overrideColumnDefinition.Value.DbColumnName ?? convColDefinition.DbColumnName;
                    convColDefinition.DbColumnAlias = overrideColumnDefinition.Value.DbColumnAlias ?? convColDefinition.DbColumnAlias;
                    convColDefinition.DbColumnType = overrideColumnDefinition.Value.DbColumnType ?? convColDefinition.DbColumnType;
                    convColDefinition.IgnoreColumn = overrideColumnDefinition.Value.IgnoreColumn ?? convColDefinition.IgnoreColumn;
                    convColDefinition.ResultColumn = overrideColumnDefinition.Value.ResultColumn ?? convColDefinition.ResultColumn;
                    convColDefinition.ComputedColumn = overrideColumnDefinition.Value.ComputedColumn ?? convColDefinition.ComputedColumn;
                    convColDefinition.VersionColumn = overrideColumnDefinition.Value.VersionColumn ?? convColDefinition.VersionColumn;
                    convColDefinition.VersionColumnType = overrideColumnDefinition.Value.VersionColumnType ?? convColDefinition.VersionColumnType;
                    convColDefinition.MemberInfo = overrideColumnDefinition.Value.MemberInfo ?? convColDefinition.MemberInfo;
                    convColDefinition.ForceUtc = overrideColumnDefinition.Value.ForceUtc ?? convColDefinition.ForceUtc;
                    convColDefinition.IsReferenceMember = overrideColumnDefinition.Value.IsReferenceMember ?? convColDefinition.IsReferenceMember;
                    convColDefinition.ReferenceMember = overrideColumnDefinition.Value.ReferenceMember ?? convColDefinition.ReferenceMember;
                }
            }
        }

        private static FluentConfig SetFactory(Mappings mappings, Action<IConventionScanner> scanner)
        {
            var maps = mappings;
            var scana = scanner;
            return new FluentConfig(mapper => new PocoDataFactory((t, aliasCache, pocoDataFactory) =>
            {
                if (maps != null)
                {
                    if (maps.Config.ContainsKey(t))
                    {
                        return new FluentMappingsPocoData(t, mappings, mapper, aliasCache, pocoDataFactory).Init();
                    }

                    if (scana != null)
                    {
                        var settings = ProcessSettings(scana);
                        var typeMapping = CreateMappings(settings, new[] { t });
                        return new FluentMappingsPocoData(t, typeMapping, mapper, aliasCache, pocoDataFactory).Init();
                    }
                }
                return new PocoData(t, mapper, aliasCache, pocoDataFactory).Init();
            }));
        }

        // Helper method if code is in seperate assembly
        private static Assembly FindTheCallingAssembly()
        {
            if (!typeof(FluentMappingConfiguration).Assembly.FullName.StartsWith("NPoco,"))
                return Assembly.GetCallingAssembly();

            var trace = new StackTrace(false);

            Assembly thisAssembly = Assembly.GetExecutingAssembly();
            Assembly callingAssembly = null;
            for (int i = 0; i < trace.FrameCount; i++)
            {
                StackFrame frame = trace.GetFrame(i);
                Assembly assembly = frame.GetMethod().DeclaringType.Assembly;
                if (assembly != thisAssembly)
                {
                    callingAssembly = assembly;
                    break;
                }
            }
            return callingAssembly;
        }
    }
}