﻿using System;
using System.Text;
using Marten.Linq;
using Marten.Schema;

namespace Marten.Services.Includes
{
    public class IncludeJoin<T> : IIncludeJoin
    {
        public const string InnerJoin = "INNER JOIN";
        public const string OuterJoin = "LEFT OUTER JOIN";

        private readonly IQueryableDocument _mapping;
        private readonly IField _field;
        private readonly Action<T> _callback;

        public IncludeJoin(IQueryableDocument mapping, IField field, string tableAlias, Action<T> callback, JoinType joinType)
        {
            _mapping = mapping;
            _field = field;
            _callback = callback;

            TableAlias = tableAlias;
            JoinType = joinType;

            IsSoftDeleted = mapping.DeleteStyle == DeleteStyle.SoftDelete;
        }

        public void AppendJoin(StringBuilder sql, string rootTableAlias, IQueryableDocument document)
        {
            var locator = document == null
                ? _field.LocatorFor(rootTableAlias)
                : document.FieldFor(_field.Members).LocatorFor(rootTableAlias);
                
            var joinOperator = JoinType == JoinType.Inner ? InnerJoin : OuterJoin;


            sql.Append(joinOperator);
            sql.Append(" ");

            if (IsSoftDeleted)
            {
                sql.Append("(select * from ");
                sql.Append(_mapping.Table.QualifiedName);
                sql.Append(" where ");
                sql.Append(DocumentMapping.DeletedColumn);
                sql.Append(" = False)");
            }
            else
            {
                sql.Append(_mapping.Table.QualifiedName);
            }

            sql.Append(" as ");
            sql.Append(TableAlias);
            sql.Append(" ON ");
            sql.Append(locator);
            sql.Append(" = ");
            sql.Append(TableAlias);
            sql.Append(".id");
        }

        public bool IsSoftDeleted { get;}

        // TODO -- remove this when we tackle moving ISelector to using StringBUilder's
        public string JoinText
        {
            get
            {
                var sql = new StringBuilder();
                AppendJoin(sql, "d", null);

                return sql.ToString();
            }
        }

        public string TableAlias { get; }
        public JoinType JoinType { get; set; }

        public ISelector<TSearched> WrapSelector<TSearched>(IDocumentSchema schema, ISelector<TSearched> inner)
        {
            return new IncludeSelector<TSearched, T>(TableAlias, _mapping, _callback, inner, schema.ResolverFor<T>());
        }
    }
}