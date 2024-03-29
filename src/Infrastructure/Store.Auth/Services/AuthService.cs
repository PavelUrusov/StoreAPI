﻿using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Store.Application.Common;
using Store.Application.Interfaces.TransactionService;
using Store.Auth.Common.DTO;
using Store.Auth.Configuration;
using Store.Auth.Interfaces;
using Store.Domain.Entities;

namespace Store.Auth.Services;

public class AuthService : IAuthService
{

    private readonly IAuthorizationContext _authorizationContext;
    private readonly string _ipAddress;
    private readonly JwtBearerConfiguration _jwtConfig;
    private readonly ILogger<AuthService> _logger;
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly ITransactionService _transactionService;
    private readonly UserManager<User> _userManager;
    public readonly CookieOptions AccessCookieOptions;
    public readonly CookieOptions RefreshCookieOptions;

    public AuthService(IOptions<JwtBearerConfiguration> jwtConfiguration, ILogger<AuthService> logger,
        IRefreshTokenRepository refreshTokenRepository, ITransactionService transactionService, UserManager<User> userManager,
        IAuthorizationContext authorizationContext)
    {
        _authorizationContext = authorizationContext;

        _ipAddress = authorizationContext.IpAddress() ??
                     throw new ArgumentNullException("Failed to determine user due to inability to determine client IP address.");

        _logger = logger;
        _refreshTokenRepository = refreshTokenRepository;
        _transactionService = transactionService;
        _userManager = userManager;
        _jwtConfig = jwtConfiguration.Value;

        RefreshCookieOptions = new CookieOptions
        {
            Expires = DateTime.UtcNow.AddDays(_jwtConfig.RefreshTokenExpiryTime.TotalDays),
            HttpOnly = true
        };

        AccessCookieOptions = new CookieOptions
        {
            Expires = DateTime.UtcNow.AddDays(_jwtConfig.AccessTokenExpiryTime.TotalMinutes),
            HttpOnly = true
        };
    }

    public async Task<ResponseBase> SignOutAsync()
    {
        var methodName = nameof(SignOutAsync);
        _logger.LogInformation($"{methodName} - Logging out the user.");

        var token = _authorizationContext.RefreshToken();
        var userId = _authorizationContext.UserId();
        var user = await _userManager.FindByIdAsync(userId);
        var refreshToken = user?.RefreshTokens.FirstOrDefault(x => x.Token == token);

        if (refreshToken == null)
        {
            _logger.LogWarning($"{methodName} - Invalid user id or refresh token.");

            return ResponseBase.Fail("Invalid or expired refresh token");
        }

        await RevokeRefreshTokenAsync(refreshToken);
        _authorizationContext.ResetAccessToken();
        _authorizationContext.ResetRefreshToken();

        return ResponseBase.Success();
    }

    public async Task<ResponseBase> RefreshTokenAsync()
    {
        var methodName = nameof(RefreshTokenAsync);
        _logger.LogInformation($"{methodName} - Starting a token refresh.");
        var token = _authorizationContext.RefreshToken();
        var userId = _authorizationContext.UserId();
        var user = await _userManager.FindByIdAsync(userId);
        var refreshToken = user.RefreshTokens.FirstOrDefault(x => x.Token == token);

        if (refreshToken == null || !IsRefreshTokenValid(refreshToken))
        {
            _logger.LogWarning($"{methodName} - Invalid user id or refresh token.");

            return ResponseBase.Fail("Invalid or expired refresh token");
        }

        var daysUntilExpiry = (refreshToken.ExpiryOn - DateTime.Now).TotalDays;

        if (daysUntilExpiry < 1)
        {
            await RevokeRefreshTokenAsync(refreshToken);
            var newRefreshToken = await GenerateRefreshTokenAsync(user);
            _authorizationContext.RefreshToken(newRefreshToken.Token, RefreshCookieOptions);
        }

        var newAccessToken = await GenerateAccessTokenAsync(user);
        _authorizationContext.AccessToken(newAccessToken, AccessCookieOptions);
        _logger.LogInformation($"{methodName} - Token successfully renewed.");

        return ResponseBase.Success();
    }

    public async Task<ResponseBase> SignUpAsync(SignUpCredentials credentials)
    {
        var methodName = nameof(SignUpAsync);
        _logger.LogInformation($"{methodName} - Starting sign up for: {credentials.Username}");

        try
        {
            await _transactionService.ExecuteInTransactionAsync(async () =>
            {
                var user = new User { UserName = credentials.Username };
                var result = await _userManager.CreateAsync(user, credentials.Password);

                if (!result.Succeeded)
                    throw new Exception($"Failed to create user: {result.Errors.FirstOrDefault()?.Description}");

                user = await _userManager.FindByNameAsync(credentials.Username);
                var roleResult = await _userManager.AddToRoleAsync(user, Roles.User);

                if (!roleResult.Succeeded)
                    throw new Exception($"Failed to add role to user: {roleResult.Errors.FirstOrDefault()?.Description}");
            }, IsolationLevel.ReadCommitted);

            _logger.LogInformation($"{methodName} - Sign up successful for: {credentials.Username}");

            return ResponseBase.Success(HttpStatusCode.Created);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"{methodName} - Sign up failed for: {credentials.Username}");

            return ResponseBase.Fail(ex.Message);
        }
    }

    public async Task<ResponseBase> SignInAsync(SignInCredentials credentials)
    {
        var methodName = nameof(SignInAsync);
        _logger.LogInformation($"{methodName} - Attempting sign in for: {credentials.Username}");
        var user = await VerifyUserAsync(credentials);

        if (user == null)
        {
            _logger.LogWarning($"{methodName} - Sign in failed for: {credentials.Username}");

            return ResponseBase.Fail("Sign-in failed. Wrong login or password");
        }

        var accessToken = await GenerateAccessTokenAsync(user);
        var refreshToken = await GenerateRefreshTokenAsync(user);
        _authorizationContext.AccessToken(accessToken, AccessCookieOptions);
        _authorizationContext.RefreshToken(refreshToken.Token, AccessCookieOptions);
        _logger.LogInformation($"{methodName} - Sign in successful for: {credentials.Username}");

        return ResponseBase.Success();
    }

    private async Task RevokeRefreshTokenAsync(RefreshToken token)
    {
        token.RevokedByIp = _ipAddress;
        token.RevokedOn = DateTime.UtcNow;
        await _refreshTokenRepository.UpdateAsync(token);
    }

    private async Task<User?> VerifyUserAsync(SignInCredentials credentials)
    {
        var user = await _userManager.FindByNameAsync(credentials.Username);

        if (user == null)
            return null;

        var result = _userManager.PasswordHasher.VerifyHashedPassword(user, user.PasswordHash, credentials.Password);

        return result == PasswordVerificationResult.Failed ? null : user;
    }

    private async Task<string> GenerateAccessTokenAsync(User user)
    {
        var userRole = await _userManager.GetRolesAsync(user);
        var roleClaims = userRole.Select(ur => new Claim(ClaimTypes.Role, ur));
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.ASCII.GetBytes(_jwtConfig.SecretKey);

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new Claim[]
            {
                new(ClaimTypes.Name, user.UserName),
                new(ClaimTypes.NameIdentifier, user.Id.ToString())
            }.Concat(roleClaims)),
            Expires = DateTime.UtcNow.AddMinutes(_jwtConfig.AccessTokenExpiryTime.TotalMinutes),
            SigningCredentials =
                new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature),
            Audience = _jwtConfig.ValidAudience,
            Issuer = _jwtConfig.ValidIssuer
        };

        var token = tokenHandler.CreateJwtSecurityToken(tokenDescriptor);

        return tokenHandler.WriteToken(token);
    }

    private async Task<RefreshToken> GenerateRefreshTokenAsync(User user)
    {
        using var randomNumberGenerator = RandomNumberGenerator.Create();
        var randomBytes = new byte[64];
        randomNumberGenerator.GetBytes(randomBytes);

        var refreshToken = new RefreshToken
        {
            Token = Convert.ToBase64String(randomBytes),
            ExpiryOn = DateTime.UtcNow.AddDays(_jwtConfig.RefreshTokenExpiryTime.TotalDays),
            CreatedOn = DateTime.UtcNow,
            CreatedByIp = _ipAddress,
            User = user
        };

        await _refreshTokenRepository.CreateAsync(refreshToken);

        return refreshToken;
    }

    private bool IsRefreshTokenValid(RefreshToken refreshToken)
    {
        if (refreshToken.RevokedByIp != null && refreshToken.RevokedOn != null)
            return false;

        return refreshToken.ExpiryOn > DateTime.UtcNow;
    }

}