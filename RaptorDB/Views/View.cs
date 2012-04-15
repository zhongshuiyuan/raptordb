﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Serialization;

namespace RaptorDB.Views
{
    internal class ViewRowDefinition
    {
        public ViewRowDefinition()
        {
            Columns = new List<KeyValuePair<string, Type>>();
        }
        public string Name { get; set; }
        internal List<KeyValuePair<string, Type>> Columns { get; set; }

        public void Add(string name, Type type)
        {
            Columns.Add(new KeyValuePair<string, Type>( name, type));
        }
    }

    public abstract class ViewBase
    {
        public Guid DocID { get; set; }
        /// <summary>
        /// Name of the view will be used for foldernames and filename and generated code
        /// </summary>
        public string Name { get; set;}
        /// <summary>
        /// A text for describing this views purpose for other developers 
        /// </summary>
        public string Description { get; set; }

        ///// <summary>
        ///// C# code for the mapper function
        ///// </summary>
        //public string MapFunctionCode { get; set; }

        /// <summary>
        /// Column definitions for the view storage 
        /// </summary>
        public Type Schema { get; set; }

        internal ViewRowDefinition SchemaColumns { get; set; }

        /// <summary>
        /// A list of Types that this view responds to (inheiratance is supported)
        /// Use AddFireOnTypes() to add to this list
        /// </summary>
        public List<string> FireOnTypes { get; set; }

        ///// <summary>
        ///// List of Views used for code generation in the mapper function
        ///// </summary>
        //public List<ViewRowDefinition> ViewsUsed { get; set; }

        /// <summary>
        /// Is this the primary list and will be populated synchronously
        /// </summary>
        public bool isPrimaryList { get; set; }

        /// <summary>
        /// Is this view active and will recieve data
        /// </summary>
        public bool isActive { get; set; }

        /// <summary>
        /// Delete items on DocID before inserting new rows (default = true)
        /// </summary>
        public bool DeleteBeforeInsert { get; set; }

        ///// <summary>
        ///// 
        ///// </summary>
        //public bool RebuildIndexesOnSchemaChange { get; set; }

        /// <summary>
        /// Index in the background : better performance but reads might not have all the data
        /// </summary>
        public bool BackgroundIndexing { get; set; }

        /// <summary>
        /// Fire the mapper on these types
        /// </summary>
        /// <param name="type"></param>
        public void AddFireOnTypes(Type type)
        {
            FireOnTypes.Add(type.AssemblyQualifiedName);
        }
    }


    public class View<T> : ViewBase
    {
        public delegate void MapFunctionDelgate<V>(Mapping.IMapAPI api, Guid docid, V doc);
        public View()
        {
            DocID = Guid.NewGuid();
            isActive = true;
            FireOnTypes = new List<string>();
            DeleteBeforeInsert = true;
            BackgroundIndexing = true;
        }

        /// <summary>
        /// Inline delegate for the mapper function used for quick applications 
        /// </summary>
        [XmlIgnore]
        public MapFunctionDelgate<T> Mapper { get; set; }

        public bool Verify()
        {
            if (Name == null || Name == "") return false;
            if (Schema == null) return false;
            if (Mapper == null) return false;
            if (FireOnTypes.Count == 0) return false;
            // FIX : add more verifications
            return true;
        }
    }
}