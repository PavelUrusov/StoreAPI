﻿using AutoMapper;
using Store.Application.Common;
using Store.Application.Mapper;
using Store.Domain.Entities;

namespace Store.Application.CQRS.Queries.ProductQueries.ReadSingle;

public record ReadProductResponse : ResponseBase, IMapWith<Product>
{

    public object Product { get; set; } = null!;

    public void Mapping(Profile profile)
    {
        profile.CreateMap<Product, ReadProductResponse>()
            .ForMember(dest => dest.Product,
                opt => opt.MapFrom(product => new
                {
                    product.Id,
                    product.Name,
                    product.Description,
                    product.Price,
                    product.Quantity,
                    Categories = product.Categories.Select(category => new
                    {
                        category.Id,
                        category.Name,
                        category.Description
                    })
                }));
    }

}